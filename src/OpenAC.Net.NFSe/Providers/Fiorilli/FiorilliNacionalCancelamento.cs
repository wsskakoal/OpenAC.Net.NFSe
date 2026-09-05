// ***********************************************************************
// Assembly         : OpenAC.Net.NFSe
// Author           : Wyllian Santos
// Created          : 04-09-2026
//
// Last Modified By : Wyllian Santos
// Last Modified On : 04-09-2026
// ***********************************************************************
// <copyright file="FiorilliNacionalCancelamento.cs" company="OpenAC .Net">
//		        		   The MIT License (MIT)
//	     		Copyright (c) 2014 - 2026 Projeto OpenAC .Net
//
//	 Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the "Software"),
// to deal in the Software without restriction, including without limitation
// the rights to use, copy, modify, merge, publish, distribute, sublicense,
// and/or sell copies of the Software, and to permit persons to whom the
// Software is furnished to do so, subject to the following conditions:
//	 The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
//	 THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT.
// IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM,
// DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE,
// ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.
// </copyright>
// <summary></summary>
// ***********************************************************************

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using OpenAC.Net.Core.Extensions;
using OpenAC.Net.DFe.Core;
using OpenAC.Net.DFe.Core.Common;
using OpenAC.Net.DFe.Core.Extensions;
using OpenAC.Net.NFSe.Commom.Client;
using OpenAC.Net.NFSe.Commom.Model;
using OpenAC.Net.NFSe.Commom.Types;
using OpenAC.Net.NFSe.Nota;

namespace OpenAC.Net.NFSe.Providers;

/// <summary>
/// Cancelamento de NFS-e pelo webservice da Fiorilli no layout nacional (IssWebWSNacional).
///
/// Com a Reforma Tributária a Fiorilli passou a desativar as operações do webservice ABRASF
/// (cancelarNfse, consultarLoteRps, consultarNfsePorRps, substituirNfse...): essas chamadas
/// voltam com o aviso "FI410 - serviço em desativação" e, nas prefeituras já atualizadas,
/// são rejeitadas. Apenas a recepção de RPS continua funcionando no ABRASF. O cancelamento
/// precisa então ser feito pelo webservice no layout nacional, que expõe a operação
/// <c>cancelarNFSe</c> (inscrição municipal + <c>pedRegEvento</c> assinado) e a operação
/// <c>consultarNfse</c>, usada aqui para descobrir a chave de acesso de 50 dígitos de uma
/// NFS-e que foi emitida pelo ABRASF (a partir do número/série do RPS, que a Fiorilli
/// registra como número/série da DPS).
///
/// Fluxo: o provedor tenta o cancelamento ABRASF normalmente; se falhar com o aviso FI410,
/// <see cref="CancelarLayoutNacional"/> consulta a chave de acesso, monta e assina o pedido
/// de registro do evento e101101 (cancelamento) e envia ao webservice nacional.
/// </summary>
internal static class FiorilliNacionalCancelamento
{
    #region Constantes

    /// <summary>
    /// Código com que a Fiorilli avisa que a operação ABRASF está em desativação.
    /// </summary>
    public const string CodigoAvisoDesativacao = "FI410";

    public const string NamespaceFiorilliNacional = "http://www.fiorilli.com.br/nfse-nacional";

    public const string NamespaceNFSeNacional = "http://www.sped.fazenda.gov.br/nfse";

    public const string VersaoLayoutNacional = "1.01";

    /// <summary>
    /// Código do evento de cancelamento (e101101) do padrão nacional.
    /// </summary>
    public const string CodigoEventoCancelamento = "101101";

    /// <summary>
    /// Código de erro do padrão nacional (SEFIN) para NFS-e que já possui evento de cancelamento.
    /// </summary>
    public const string CodigoJaCancelada = "E0840";

    private const string PrefixoNacional = "nac";

    private const string CaminhoWebserviceAbrasf = "/IssWeb-ejb/";

    private const string CaminhoWebserviceNacional = "/IssWeb-ejb/IssWebWSNacional/IssWebWSNacionalPortType";

    private const string ChaveParametroUrlNacional = "UrlNacional";

    private const string MotivoPadrao = "Cancelamento da NFS-e solicitado pelo prestador";

    private const string PrefixoMotivoCurto = "Cancelamento da NFS-e: ";

    private const int TamanhoMinimoMotivo = 15;

    private const int TamanhoMaximoMotivo = 255;

    private static readonly Regex RegexDesativacao = new(@"desativa|descontinu", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex RegexContextoWebservice = new(@"servi[cç]o|webservice|web service|nacional|abrasf|opera[cç][aã]o", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex RegexJaCancelada = new(@"j[aá]\s+(est[aá]\s+|foi\s+|se\s+encontra\s+)?cancelad", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex RegexStatusErro = new(@"erro|rejei|falha|inv[aá]lid", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex RegexChaveAcesso = new(@"^\d{50}$", RegexOptions.Compiled);

    #endregion Constantes

    #region Aviso de desativação (FI410)

    /// <summary>
    /// Identifica o aviso de desativação do webservice ABRASF: código FI410 ou texto falando em
    /// desativação/descontinuação do serviço. Exige o contexto de "serviço/webservice" para não
    /// confundir com mensagens como "contribuinte desativado".
    /// </summary>
    public static bool IsAvisoDesativacao(EventoRetorno evento)
    {
        if (evento == null) return false;

        if (string.Equals(evento.Codigo?.Trim(), CodigoAvisoDesativacao, StringComparison.OrdinalIgnoreCase))
            return true;

        var descricao = evento.Descricao ?? string.Empty;
        return RegexDesativacao.IsMatch(descricao) && RegexContextoWebservice.IsMatch(descricao);
    }

    /// <summary>
    /// Verifica se o retorno (erros ou alertas) contém o aviso de desativação do ABRASF.
    /// </summary>
    public static bool ContemAvisoDesativacao(RetornoWebservice retorno) =>
        retorno != null && retorno.Erros.Concat(retorno.Alertas).Any(IsAvisoDesativacao);

    /// <summary>
    /// Move o aviso de desativação da lista de erros para a de alertas. O aviso vem dentro de
    /// ListaMensagemRetorno e, sem isso, o parser trataria a resposta inteira como rejeição,
    /// ignorando a confirmação/dados que possam ter vindo junto.
    /// </summary>
    public static void ReclassificarAvisosDesativacao(RetornoWebservice retorno)
    {
        if (retorno == null) return;

        var avisos = retorno.Erros.Where(IsAvisoDesativacao).ToList();
        foreach (var aviso in avisos)
        {
            retorno.Erros.Remove(aviso);
            retorno.Alertas.Add(aviso);
        }
    }

    #endregion Aviso de desativação (FI410)

    #region Montagem dos XMLs

    /// <summary>
    /// O padrão nacional exige xMotivo entre 15 e 255 caracteres; o ABRASF não usa o motivo,
    /// então ele pode chegar vazio ou curto demais.
    /// </summary>
    public static string NormalizarMotivo(string? motivo)
    {
        var texto = (motivo ?? string.Empty).Trim();

        if (texto.Length == 0)
            texto = MotivoPadrao;
        else if (texto.Length < TamanhoMinimoMotivo)
            texto = PrefixoMotivoCurto + texto;

        return texto.Length > TamanhoMaximoMotivo ? texto.Substring(0, TamanhoMaximoMotivo) : texto;
    }

    /// <summary>
    /// Converte o código de cancelamento ABRASF (1 = erro na emissão, 2 = serviço não prestado,
    /// 3 = erro de assinatura, 4 = duplicidade, 5 = erro de processamento) para o cMotivo do
    /// evento nacional (1 = erro na emissão, 2 = serviço não prestado, 9 = outros).
    /// </summary>
    public static string MapearMotivo(string? codigoCancelamentoAbrasf)
    {
        return (codigoCancelamentoAbrasf ?? string.Empty).Trim() switch
        {
            "1" => "1",
            "2" => "2",
            _ => "9"
        };
    }

    /// <summary>
    /// Deriva a URL do webservice nacional a partir da URL ABRASF do município
    /// (https://host/IssWeb-ejb/IssWebWS/IssWebWS?wsdl -> https://host/IssWeb-ejb/IssWebWSNacional/IssWebWSNacionalPortType).
    /// </summary>
    public static string DerivarUrlNacional(string urlAbrasf)
    {
        if (Vazio(urlAbrasf))
            throw new OpenDFeException("URL do webservice ABRASF da Fiorilli não informada para o município.");

        var indice = urlAbrasf.IndexOf(CaminhoWebserviceAbrasf, StringComparison.OrdinalIgnoreCase);
        if (indice < 0)
            throw new OpenDFeException($"Não foi possível derivar a URL do webservice nacional da Fiorilli a partir de \"{urlAbrasf}\". Informe o parâmetro \"{ChaveParametroUrlNacional}\" do município.");

        return urlAbrasf.Substring(0, indice) + CaminhoWebserviceNacional;
    }

    /// <summary>
    /// URL do webservice nacional: parâmetro "UrlNacional" do município, se cadastrado; senão derivada da URL de cancelamento ABRASF.
    /// </summary>
    public static string ObterUrlNacional(ProviderBase provider)
    {
        var parametros = provider.Municipio.Parametros;
        if (parametros != null && parametros.TryGetValue(ChaveParametroUrlNacional, out var urlParametro) && !Vazio(urlParametro))
            return urlParametro!.Replace("?wsdl", "");

        return DerivarUrlNacional(provider.GetUrl(TipoUrl.CancelarNFSe));
    }

    /// <summary>
    /// Monta o ConsultarNfseEnvio (consulta por CNPJ/CPF, inscrição municipal e número/série da DPS).
    /// </summary>
    public static string MontarXmlConsultaNfse(string cpfCnpj, string? inscricaoMunicipal, string numeroDps, string? serieDps)
    {
        var documento = SomenteDigitos(cpfCnpj);
        var xml = new StringBuilder();
        xml.Append($"<{PrefixoNacional}:ConsultarNfseEnvio xmlns:{PrefixoNacional}=\"{NamespaceFiorilliNacional}\">");
        xml.Append(documento.IsCNPJ()
            ? $"<{PrefixoNacional}:CNPJ>{documento.ZeroFill(14)}</{PrefixoNacional}:CNPJ>"
            : $"<{PrefixoNacional}:CPF>{documento.ZeroFill(11)}</{PrefixoNacional}:CPF>");
        if (!Vazio(inscricaoMunicipal))
            xml.Append($"<{PrefixoNacional}:IM>{EscaparXml(inscricaoMunicipal!)}</{PrefixoNacional}:IM>");
        xml.Append($"<{PrefixoNacional}:NumeroDPS>{EscaparXml(numeroDps.Trim())}</{PrefixoNacional}:NumeroDPS>");
        if (!Vazio(serieDps))
            xml.Append($"<{PrefixoNacional}:SerieDPS>{EscaparXml(serieDps!.Trim())}</{PrefixoNacional}:SerieDPS>");
        xml.Append($"</{PrefixoNacional}:ConsultarNfseEnvio>");
        return xml.ToString();
    }

    /// <summary>
    /// Extrai, da resposta do consultarNfse, a chave de acesso (Id="NFS" + 50 dígitos de infNFSe)
    /// da NFS-e cujo nNFSe é o número informado. Só aceita a nota com o mesmo número: cancelar a
    /// nota errada é irreversível.
    /// </summary>
    public static string? ExtrairChaveAcesso(string xmlRespostaConsulta, string numeroNFSe)
    {
        if (Vazio(xmlRespostaConsulta)) return null;

        var documento = XDocument.Parse(xmlRespostaConsulta);
        var notas = documento.Descendants().Where(x => x.Name.LocalName == "infNFSe");

        foreach (var infNFSe in notas)
        {
            var numero = infNFSe.ElementAnyNs("nNFSe")?.Value;
            if (!NumeroIgual(numero, numeroNFSe)) continue;

            var id = infNFSe.Attribute("Id")?.Value ?? string.Empty;
            var chave = id.StartsWith("NFS", StringComparison.OrdinalIgnoreCase) ? id.Substring(3) : id;
            if (RegexChaveAcesso.IsMatch(chave)) return chave;
        }

        return null;
    }

    /// <summary>
    /// Monta o pedRegEvento (layout nacional 1.01) com o evento de cancelamento e101101.
    /// </summary>
    public static string MontarXmlPedRegEvento(string chaveAcesso, DFeTipoAmbiente ambiente, string cpfCnpjAutor, string codigoMotivo, string motivo, DateTimeOffset dhEvento)
    {
        var documento = SomenteDigitos(cpfCnpjAutor);
        var xml = new StringBuilder();
        xml.Append($"<pedRegEvento versao=\"{VersaoLayoutNacional}\" xmlns=\"{NamespaceNFSeNacional}\">");
        xml.Append($"<infPedReg Id=\"{MontarIdPedRegEvento(chaveAcesso)}\">");
        xml.Append($"<tpAmb>{(ambiente == DFeTipoAmbiente.Producao ? "1" : "2")}</tpAmb>");
        xml.Append("<verAplic>OpenAC.Net.NFSe</verAplic>");
        xml.Append($"<dhEvento>{dhEvento.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture)}</dhEvento>");
        xml.Append(documento.IsCNPJ()
            ? $"<CNPJAutor>{documento.ZeroFill(14)}</CNPJAutor>"
            : $"<CPFAutor>{documento.ZeroFill(11)}</CPFAutor>");
        xml.Append($"<chNFSe>{chaveAcesso}</chNFSe>");
        xml.Append($"<e{CodigoEventoCancelamento}>");
        xml.Append("<xDesc>Cancelamento de NFS-e</xDesc>");
        xml.Append($"<cMotivo>{codigoMotivo}</cMotivo>");
        xml.Append($"<xMotivo>{EscaparXml(motivo)}</xMotivo>");
        xml.Append($"</e{CodigoEventoCancelamento}>");
        xml.Append("</infPedReg>");
        xml.Append("</pedRegEvento>");
        return xml.ToString();
    }

    public static string MontarIdPedRegEvento(string chaveAcesso) => $"PRE{chaveAcesso}{CodigoEventoCancelamento}";

    /// <summary>
    /// Assina o infPedReg (referência #Id) com canonicalização exclusiva (xml-exc-c14n), como a
    /// Fiorilli orienta em comunicado técnico: o pedido trafega dentro do envelope SOAP e a
    /// canonicalização inclusiva herdaria namespaces do envelope, quebrando a validação.
    /// Assinatura RSA-SHA1/SHA1, a mesma aceita pelo ambiente nacional.
    /// </summary>
    public static string AssinarPedRegEvento(string xml, X509Certificate2 certificado)
    {
        if (certificado == null)
            throw new OpenDFeException("Certificado digital não informado para assinar o pedido de cancelamento no layout nacional.");

        var documento = new XmlDocument { PreserveWhitespace = true };
        documento.LoadXml(xml);

        var infPedReg = documento.GetElementsByTagName("infPedReg").Cast<XmlElement>().FirstOrDefault()
            ?? throw new OpenDFeException("Elemento infPedReg não encontrado para assinatura.");
        var id = infPedReg.GetAttribute("Id");
        if (Vazio(id))
            throw new OpenDFeException("Atributo Id do infPedReg não encontrado para assinatura.");

        var chavePrivada = certificado.GetRSAPrivateKey()
            ?? throw new OpenDFeException("O certificado digital não possui chave privada RSA para assinatura.");

        var assinatura = new SignedXml(documento) { SigningKey = chavePrivada };
        assinatura.SignedInfo!.CanonicalizationMethod = SignedXml.XmlDsigExcC14NTransformUrl;
        assinatura.SignedInfo!.SignatureMethod = SignedXml.XmlDsigRSASHA1Url;

        var referencia = new Reference($"#{id}") { DigestMethod = SignedXml.XmlDsigSHA1Url };
        referencia.AddTransform(new XmlDsigEnvelopedSignatureTransform());
        referencia.AddTransform(new XmlDsigExcC14NTransform());
        assinatura.AddReference(referencia);

        var keyInfo = new KeyInfo();
        keyInfo.AddClause(new KeyInfoX509Data(certificado));
        assinatura.KeyInfo = keyInfo;

        assinatura.ComputeSignature();
        documento.DocumentElement!.AppendChild(documento.ImportNode(assinatura.GetXml(), true));

        return documento.DocumentElement!.OuterXml;
    }

    /// <summary>
    /// Monta o CancelarNFSeEnvio do webservice da Fiorilli. Pelo WSDL, IM e pedRegEvento ficam no
    /// namespace da Fiorilli (form="qualified") e o conteúdo do pedido (infPedReg...) no namespace
    /// nacional; por isso só a tag raiz do pedido assinado é renomeada, sem tocar nos bytes assinados
    /// (a canonicalização exclusiva garante que a assinatura continue válida).
    /// </summary>
    public static string MontarXmlCancelamento(string? inscricaoMunicipal, string pedRegEventoAssinado)
    {
        var raizOriginal = Regex.Match(pedRegEventoAssinado, @"^\s*<pedRegEvento\b[^>]*>", RegexOptions.Singleline);
        if (!raizOriginal.Success || !pedRegEventoAssinado.TrimEnd().EndsWith("</pedRegEvento>", StringComparison.Ordinal))
            throw new OpenDFeException("XML do pedRegEvento assinado em formato inesperado.");

        var corpo = pedRegEventoAssinado.Substring(raizOriginal.Index + raizOriginal.Length);
        corpo = corpo.Substring(0, corpo.LastIndexOf("</pedRegEvento>", StringComparison.Ordinal));

        var xml = new StringBuilder();
        xml.Append($"<{PrefixoNacional}:CancelarNFSeEnvio xmlns:{PrefixoNacional}=\"{NamespaceFiorilliNacional}\">");
        xml.Append($"<{PrefixoNacional}:IM>{EscaparXml(inscricaoMunicipal ?? string.Empty)}</{PrefixoNacional}:IM>");
        xml.Append($"<{PrefixoNacional}:pedRegEvento versao=\"{VersaoLayoutNacional}\" xmlns=\"{NamespaceNFSeNacional}\">");
        xml.Append(corpo);
        xml.Append($"</{PrefixoNacional}:pedRegEvento>");
        xml.Append($"</{PrefixoNacional}:CancelarNFSeEnvio>");
        return xml.ToString();
    }

    #endregion Montagem dos XMLs

    #region Tratamento das respostas

    /// <summary>
    /// Interpreta o CancelarNFSeResposta (DataRecebimento, status, ListaMensagens/mensagem).
    /// Mensagens com código iniciado em "A" são alertas; as demais, erros. NFS-e já cancelada
    /// (E0840 ou texto "já cancelada") é tratada como sucesso, com alerta.
    /// </summary>
    public static void TratarRespostaCancelamento(string xmlResposta, RetornoCancelar retorno)
    {
        retorno.Sucesso = false;

        if (Vazio(xmlResposta))
        {
            retorno.Erros.Add(new EventoRetorno { Codigo = "0", Descricao = "Resposta vazia do webservice nacional da Fiorilli (cancelarNFSe)." });
            return;
        }

        var documento = XDocument.Parse(xmlResposta);
        var resposta = documento.Root?.Name.LocalName == "CancelarNFSeResposta"
            ? documento.Root
            : documento.Root?.ElementAnyNs("CancelarNFSeResposta") ?? documento.Root;

        if (resposta == null)
        {
            retorno.Erros.Add(new EventoRetorno { Codigo = "0", Descricao = "Resposta do webservice nacional da Fiorilli (cancelarNFSe) sem conteúdo." });
            return;
        }

        var mensagens = ExtrairMensagens(resposta).ToList();
        var jaCancelada = mensagens.Any(IsJaCancelada);

        foreach (var mensagem in mensagens)
        {
            if (jaCancelada || IsMensagemAlerta(mensagem))
                retorno.Alertas.Add(mensagem);
            else
                retorno.Erros.Add(mensagem);
        }

        var dataRecebimento = LerDataHora(resposta.ElementAnyNs("DataRecebimento")?.Value);
        var status = resposta.ElementAnyNs("status")?.Value?.Trim() ?? string.Empty;

        if (jaCancelada)
        {
            retorno.Alertas.Add(new EventoRetorno { Codigo = CodigoJaCancelada, Descricao = "A NFS-e já constava cancelada na prefeitura; situação sincronizada." });
            retorno.Data = dataRecebimento ?? DateTime.Now;
            retorno.Sucesso = true;
            return;
        }

        if (retorno.Erros.Any()) return;

        if (!Vazio(status) && RegexStatusErro.IsMatch(status))
        {
            retorno.Erros.Add(new EventoRetorno { Codigo = "0", Descricao = $"Webservice nacional da Fiorilli retornou status \"{status}\" para o cancelamento." });
            return;
        }

        if (dataRecebimento == null && Vazio(status))
        {
            retorno.Erros.Add(new EventoRetorno { Codigo = "0", Descricao = "Resposta do cancelamento no layout nacional não reconhecida (sem DataRecebimento e sem status)." });
            return;
        }

        retorno.Data = dataRecebimento ?? DateTime.Now;
        retorno.Sucesso = true;
    }

    /// <summary>
    /// Lê ListaMensagens/mensagem (Codigo, Mensagem, Correcao) de qualquer resposta do webservice nacional.
    /// </summary>
    public static IEnumerable<EventoRetorno> ExtrairMensagens(XElement? resposta)
    {
        if (resposta == null) yield break;

        foreach (var mensagem in resposta.Descendants().Where(x => x.Name.LocalName == "mensagem"))
        {
            yield return new EventoRetorno
            {
                Codigo = mensagem.ElementAnyNs("Codigo")?.Value?.Trim() ?? string.Empty,
                Descricao = mensagem.ElementAnyNs("Mensagem")?.Value?.Trim() ?? string.Empty,
                Correcao = mensagem.ElementAnyNs("Correcao")?.Value?.Trim() ?? string.Empty
            };
        }
    }

    public static bool IsJaCancelada(EventoRetorno evento) =>
        string.Equals(evento.Codigo?.Trim(), CodigoJaCancelada, StringComparison.OrdinalIgnoreCase) ||
        (!Vazio(evento.Descricao) && RegexJaCancelada.IsMatch(evento.Descricao));

    private static bool IsMensagemAlerta(EventoRetorno evento) =>
        !Vazio(evento.Codigo) && evento.Codigo.Trim().StartsWith("A", StringComparison.OrdinalIgnoreCase);

    #endregion Tratamento das respostas

    #region Orquestração

    /// <summary>
    /// Cancela a NFS-e pelo webservice nacional da Fiorilli depois de o ABRASF ter falhado com o
    /// aviso de desativação. O resultado ABRASF é preservado como alerta; erros/sucesso passam a
    /// refletir a tentativa nacional. Exige a nota em <paramref name="notas"/> com o RPS
    /// (número/série) preenchido para localizar a chave de acesso.
    /// </summary>
    public static void CancelarLayoutNacional(ProviderBase provider, RetornoCancelar retorno, NotaServicoCollection notas)
    {
        foreach (var erro in retorno.Erros.ToList())
            retorno.Alertas.Add(new EventoRetorno { Codigo = erro.Codigo, Descricao = "[ABRASF] " + erro.Descricao, Correcao = erro.Correcao });
        retorno.Erros.Clear();
        retorno.Sucesso = false;

        var nota = notas?.FirstOrDefault(x => NumeroIgual(x.IdentificacaoNFSe?.Numero, retorno.NumeroNFSe));
        var numeroDps = nota?.IdentificacaoRps?.Numero?.Trim();
        var serieDps = nota?.IdentificacaoRps?.Serie;
        if (Vazio(serieDps)) serieDps = retorno.SerieNFSe;

        if (Vazio(numeroDps))
        {
            retorno.Erros.Add(new EventoRetorno
            {
                Codigo = "0",
                Descricao = $"O webservice ABRASF da Fiorilli não aceita mais o cancelamento (aviso {CodigoAvisoDesativacao}) e, para cancelar pelo layout nacional, é preciso informar o RPS (número/série) da NFS-e nº {retorno.NumeroNFSe} em NotasServico para localizar a chave de acesso."
            });
            return;
        }

        var prestador = provider.Configuracoes.PrestadorPadrao;
        var cpfCnpj = SomenteDigitos(prestador.CpfCnpj);

        string urlNacional;
        try
        {
            urlNacional = ObterUrlNacional(provider);
        }
        catch (Exception ex)
        {
            retorno.Erros.Add(new EventoRetorno { Codigo = "0", Descricao = "Erro em CancelarNFSe (layout nacional): " + ex.Message });
            return;
        }

        var cliente = new FiorilliNacionalServiceClient(provider, urlNacional);

        // 1) Chave de acesso da NFS-e (a partir do RPS/DPS)
        string? chaveAcesso;
        try
        {
            var xmlConsulta = MontarXmlConsultaNfse(cpfCnpj, prestador.InscricaoMunicipal, numeroDps!, serieDps);
            var respostaConsulta = cliente.ConsultarNfse(xmlConsulta);
            chaveAcesso = ExtrairChaveAcesso(respostaConsulta, retorno.NumeroNFSe);

            if (chaveAcesso == null && !Vazio(serieDps))
            {
                // A Fiorilli pode ter registrado a DPS com série diferente da do RPS: repete só pelo número.
                xmlConsulta = MontarXmlConsultaNfse(cpfCnpj, prestador.InscricaoMunicipal, numeroDps!, null);
                respostaConsulta = cliente.ConsultarNfse(xmlConsulta);
                chaveAcesso = ExtrairChaveAcesso(respostaConsulta, retorno.NumeroNFSe);
            }

            if (chaveAcesso == null)
            {
                retorno.XmlEnvio = xmlConsulta;
                retorno.XmlRetorno = respostaConsulta;
                retorno.EnvelopeEnvio = cliente.EnvelopeEnvio;
                retorno.EnvelopeRetorno = cliente.EnvelopeRetorno;

                var mensagensConsulta = ExtrairMensagens(XDocument.Parse(respostaConsulta).Root)
                    .Select(x => $"{x.Codigo} - {x.Descricao}".Trim(' ', '-'))
                    .Where(x => !Vazio(x))
                    .ToList();
                var detalhe = mensagensConsulta.Count > 0 ? " Retorno da consulta: " + string.Join("; ", mensagensConsulta) : string.Empty;

                retorno.Erros.Add(new EventoRetorno
                {
                    Codigo = "0",
                    Descricao = $"Cancelamento pelo layout nacional: a consulta ao webservice nacional da Fiorilli (consultarNfse) não localizou a NFS-e nº {retorno.NumeroNFSe} a partir do RPS {numeroDps}/{serieDps}, portanto a chave de acesso não pôde ser obtida.{detalhe}"
                });
                return;
            }
        }
        catch (Exception ex)
        {
            retorno.EnvelopeEnvio = cliente.EnvelopeEnvio;
            retorno.EnvelopeRetorno = cliente.EnvelopeRetorno;
            retorno.Erros.Add(new EventoRetorno { Codigo = "0", Descricao = "Erro em CancelarNFSe (layout nacional - consulta da chave de acesso): " + ex.Message });
            return;
        }

        retorno.Alertas.Add(new EventoRetorno { Codigo = "0", Descricao = $"Chave de acesso nacional da NFS-e nº {retorno.NumeroNFSe}: {chaveAcesso}." });

        // 2) Evento de cancelamento (e101101) assinado, enviado ao webservice nacional
        try
        {
            var motivo = NormalizarMotivo(retorno.Motivo);
            var xmlEvento = MontarXmlPedRegEvento(chaveAcesso, provider.Configuracoes.WebServices.Ambiente, cpfCnpj, MapearMotivo(retorno.CodigoCancelamento), motivo, DateTimeOffset.Now);
            var xmlAssinado = AssinarPedRegEvento(xmlEvento, provider.Certificado);
            var xmlEnvio = MontarXmlCancelamento(prestador.InscricaoMunicipal, xmlAssinado);
            retorno.XmlEnvio = xmlEnvio;

            var xmlResposta = cliente.CancelarNFSe(xmlEnvio);
            retorno.XmlRetorno = xmlResposta;
            retorno.EnvelopeEnvio = cliente.EnvelopeEnvio;
            retorno.EnvelopeRetorno = cliente.EnvelopeRetorno;

            TratarRespostaCancelamento(xmlResposta, retorno);
        }
        catch (Exception ex)
        {
            retorno.EnvelopeEnvio = cliente.EnvelopeEnvio;
            retorno.EnvelopeRetorno = cliente.EnvelopeRetorno;
            retorno.Erros.Add(new EventoRetorno { Codigo = "0", Descricao = "Erro em CancelarNFSe (layout nacional): " + ex.Message });
            return;
        }

        if (!retorno.Sucesso) return;

        retorno.Alertas.Add(new EventoRetorno { Codigo = "0", Descricao = $"NFS-e nº {retorno.NumeroNFSe} cancelada pelo webservice da Fiorilli no layout nacional." });

        if (nota == null) return;

        nota.Situacao = SituacaoNFSeRps.Cancelado;
        nota.Cancelamento.Pedido.CodigoCancelamento = retorno.CodigoCancelamento;
        nota.Cancelamento.DataHora = retorno.Data;
        nota.Cancelamento.MotivoCancelamento = retorno.Motivo;
    }

    #endregion Orquestração

    #region Utilitários

    public static bool NumeroIgual(string? a, string? b)
    {
        var digitosA = SomenteDigitos(a).TrimStart('0');
        var digitosB = SomenteDigitos(b).TrimStart('0');
        return digitosA.Length > 0 && digitosA == digitosB;
    }

    private static bool Vazio(string? texto) => string.IsNullOrWhiteSpace(texto);

    private static string SomenteDigitos(string? texto) =>
        Vazio(texto) ? string.Empty : new string(texto!.Where(char.IsDigit).ToArray());

    private static DateTime? LerDataHora(string? valor)
    {
        if (Vazio(valor)) return null;
        return DateTime.TryParse(valor, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var data) ? data : null;
    }

    private static string EscaparXml(string texto) =>
        texto.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&apos;");

    #endregion Utilitários
}

/// <summary>
/// Cliente SOAP 1.1 (document/literal) do webservice da Fiorilli no layout nacional
/// (IssWebWSNacional). Reaproveita a infraestrutura HTTP/certificado do provedor, apenas
/// apontando para a URL nacional.
/// </summary>
internal sealed class FiorilliNacionalServiceClient : NFSeSoapServiceClient
{
    public FiorilliNacionalServiceClient(ProviderBase provider, string urlNacional) : base(provider, TipoUrl.CancelarNFSe, SoapVersion.Soap11)
    {
        Url = urlNacional;
    }

    public string ConsultarNfse(string msg) => Execute("consultarNfse", msg, "", ["ConsultarNfseResposta"], []);

    public string CancelarNFSe(string msg) => Execute("cancelarNFSe", msg, "", ["CancelarNFSeResposta"], []);

    protected override string TratarRetorno(XElement xmlDocument, string[] responseTag)
    {
        var fault = xmlDocument.ElementAnyNs("Fault");
        if (fault != null)
        {
            var mensagem = $"{fault.ElementAnyNs("faultcode")?.GetValue<string>()} - {fault.ElementAnyNs("faultstring")?.GetValue<string>()}";
            throw new OpenDFeCommunicationException(mensagem);
        }

        var resposta = xmlDocument.ElementAnyNs(responseTag[0])
            ?? throw new OpenDFeCommunicationException($"Elemento {responseTag[0]} não encontrado no retorno do webservice nacional da Fiorilli.");

        return resposta.ToString(SaveOptions.DisableFormatting);
    }
}
