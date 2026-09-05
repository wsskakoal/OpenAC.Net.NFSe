using System;
using System.Linq;
using System.Xml.Linq;
using OpenAC.Net.DFe.Core.Common;
using OpenAC.Net.NFSe.Commom.Model;
using OpenAC.Net.NFSe.Providers;
using Xunit;

namespace OpenAC.Net.NFSe.Test;

/// <summary>
/// Testes puros (sem webservice) do cancelamento Fiorilli pelo layout nacional:
/// classificação do aviso FI410, montagem dos XMLs e leitura das respostas.
/// </summary>
public class TestFiorilliNacional
{
    private const string ChaveAcesso = "11000230100000000000000000000000000000000000000019";

    [Theory]
    [InlineData("FI410", "qualquer texto", true)]
    [InlineData(" fi410 ", "", true)]
    [InlineData("E999", "O serviço cancelarNfse está em desativação, utilize o webservice nacional", true)]
    [InlineData("0", "Erro em CancelarNFSe: Webservice ABRASF descontinuado pela prefeitura", true)]
    [InlineData("E123", "Contribuinte desativado", false)]
    [InlineData("E79", "NFS-e já cancelada", false)]
    [InlineData("", "", false)]
    public void IsAvisoDesativacao_ReconheceSomenteAvisoDoWebservice(string codigo, string descricao, bool esperado)
    {
        var evento = new EventoRetorno { Codigo = codigo, Descricao = descricao };

        Assert.Equal(esperado, FiorilliNacionalCancelamento.IsAvisoDesativacao(evento));
    }

    [Fact]
    public void ReclassificarAvisosDesativacao_MoveFI410ParaAlertasEMantemDemaisErros()
    {
        var retorno = new RetornoCancelar();
        retorno.Erros.Add(new EventoRetorno { Codigo = "FI410", Descricao = "Serviço em desativação" });
        retorno.Erros.Add(new EventoRetorno { Codigo = "E10", Descricao = "Nota não encontrada" });

        FiorilliNacionalCancelamento.ReclassificarAvisosDesativacao(retorno);

        Assert.Single(retorno.Erros);
        Assert.Equal("E10", retorno.Erros[0].Codigo);
        Assert.Single(retorno.Alertas);
        Assert.Equal("FI410", retorno.Alertas[0].Codigo);
        Assert.True(FiorilliNacionalCancelamento.ContemAvisoDesativacao(retorno));
    }

    [Theory]
    [InlineData(null, "Cancelamento da NFS-e solicitado pelo prestador")]
    [InlineData("   ", "Cancelamento da NFS-e solicitado pelo prestador")]
    [InlineData("erro", "Cancelamento da NFS-e: erro")]
    [InlineData("Valor lançado errado", "Valor lançado errado")]
    public void NormalizarMotivo_GaranteTamanhoMinimoDoLayoutNacional(string? motivo, string esperado)
    {
        var resultado = FiorilliNacionalCancelamento.NormalizarMotivo(motivo);

        Assert.Equal(esperado, resultado);
        Assert.True(resultado.Length >= 15);
    }

    [Fact]
    public void NormalizarMotivo_TruncaEm255Caracteres()
    {
        var resultado = FiorilliNacionalCancelamento.NormalizarMotivo(new string('x', 300));

        Assert.Equal(255, resultado.Length);
    }

    [Theory]
    [InlineData("1", "1")]
    [InlineData("2", "2")]
    [InlineData("4", "9")]
    [InlineData(null, "9")]
    public void MapearMotivo_ConverteCodigoAbrasfParaCMotivoNacional(string? codigoAbrasf, string esperado)
    {
        Assert.Equal(esperado, FiorilliNacionalCancelamento.MapearMotivo(codigoAbrasf));
    }

    [Theory]
    [InlineData("https://nfse.ariquemes.ro.gov.br/IssWeb-ejb/IssWebWS/IssWebWS?wsdl", "https://nfse.ariquemes.ro.gov.br/IssWeb-ejb/IssWebWSNacional/IssWebWSNacionalPortType")]
    [InlineData("http://fi1.fiorilli.com.br:5663/IssWeb-ejb/IssWebWS/IssWebWS?wsdl", "http://fi1.fiorilli.com.br:5663/IssWeb-ejb/IssWebWSNacional/IssWebWSNacionalPortType")]
    public void DerivarUrlNacional_TrocaOCaminhoDoWebserviceAbrasfPeloNacional(string urlAbrasf, string esperado)
    {
        Assert.Equal(esperado, FiorilliNacionalCancelamento.DerivarUrlNacional(urlAbrasf));
    }

    [Fact]
    public void DerivarUrlNacional_FalhaQuandoUrlNaoEDoIssWeb()
    {
        Assert.ThrowsAny<Exception>(() => FiorilliNacionalCancelamento.DerivarUrlNacional("https://outro.provedor.com.br/ws?wsdl"));
    }

    [Fact]
    public void MontarXmlConsultaNfse_UsaNamespaceDaFiorilliEDadosDoPrestador()
    {
        var xml = FiorilliNacionalCancelamento.MontarXmlConsultaNfse("01.001.001/0001-13", "15000", "123", "1");

        var doc = XDocument.Parse(xml);
        XNamespace nac = FiorilliNacionalCancelamento.NamespaceFiorilliNacional;
        Assert.Equal(nac + "ConsultarNfseEnvio", doc.Root!.Name);
        Assert.Equal("01001001000113", doc.Root.Element(nac + "CNPJ")!.Value);
        Assert.Equal("15000", doc.Root.Element(nac + "IM")!.Value);
        Assert.Equal("123", doc.Root.Element(nac + "NumeroDPS")!.Value);
        Assert.Equal("1", doc.Root.Element(nac + "SerieDPS")!.Value);
    }

    [Fact]
    public void ExtrairChaveAcesso_RetornaChaveDaNotaComOMesmoNumero()
    {
        var xml = RespostaConsulta(("000000000000122", "11000230100000000000000000000000000000000000000018"), ("000000000000123", ChaveAcesso));

        var chave = FiorilliNacionalCancelamento.ExtrairChaveAcesso(xml, "123");

        Assert.Equal(ChaveAcesso, chave);
    }

    [Fact]
    public void ExtrairChaveAcesso_RetornaNuloQuandoNumeroNaoConfere()
    {
        var xml = RespostaConsulta(("000000000000122", ChaveAcesso));

        Assert.Null(FiorilliNacionalCancelamento.ExtrairChaveAcesso(xml, "123"));
    }

    [Fact]
    public void MontarXmlPedRegEvento_GeraEventoDeCancelamentoDoLayoutNacional()
    {
        var dhEvento = new DateTimeOffset(2026, 9, 4, 10, 30, 0, TimeSpan.FromHours(-4));

        var xml = FiorilliNacionalCancelamento.MontarXmlPedRegEvento(ChaveAcesso, DFeTipoAmbiente.Producao, "01001001000113", "1", "Valor lançado errado & <corrigido>", dhEvento);

        XNamespace ns = FiorilliNacionalCancelamento.NamespaceNFSeNacional;
        var doc = XDocument.Parse(xml);
        Assert.Equal(ns + "pedRegEvento", doc.Root!.Name);
        Assert.Equal("1.01", doc.Root.Attribute("versao")!.Value);
        var inf = doc.Root.Element(ns + "infPedReg")!;
        Assert.Equal("PRE" + ChaveAcesso + "101101", inf.Attribute("Id")!.Value);
        Assert.Equal("1", inf.Element(ns + "tpAmb")!.Value);
        Assert.Equal("2026-09-04T10:30:00-04:00", inf.Element(ns + "dhEvento")!.Value);
        Assert.Equal("01001001000113", inf.Element(ns + "CNPJAutor")!.Value);
        Assert.Equal(ChaveAcesso, inf.Element(ns + "chNFSe")!.Value);
        var evento = inf.Element(ns + "e101101")!;
        Assert.Equal("1", evento.Element(ns + "cMotivo")!.Value);
        Assert.Equal("Valor lançado errado & <corrigido>", evento.Element(ns + "xMotivo")!.Value);
    }

    [Fact]
    public void MontarXmlCancelamento_RenomeiaRaizParaNamespaceFiorilliSemAlterarConteudoAssinado()
    {
        var conteudo = "<infPedReg Id=\"PRE" + ChaveAcesso + "101101\"><tpAmb>1</tpAmb></infPedReg><Signature xmlns=\"http://www.w3.org/2000/09/xmldsig#\"><SignedInfo/></Signature>";
        var pedidoAssinado = "<pedRegEvento versao=\"1.01\" xmlns=\"" + FiorilliNacionalCancelamento.NamespaceNFSeNacional + "\">" + conteudo + "</pedRegEvento>";

        var xml = FiorilliNacionalCancelamento.MontarXmlCancelamento("15000", pedidoAssinado);

        Assert.Contains(conteudo, xml);
        XNamespace nac = FiorilliNacionalCancelamento.NamespaceFiorilliNacional;
        XNamespace ns = FiorilliNacionalCancelamento.NamespaceNFSeNacional;
        var doc = XDocument.Parse(xml);
        Assert.Equal(nac + "CancelarNFSeEnvio", doc.Root!.Name);
        Assert.Equal("15000", doc.Root.Element(nac + "IM")!.Value);
        var pedido = doc.Root.Element(nac + "pedRegEvento")!;
        Assert.Equal("1.01", pedido.Attribute("versao")!.Value);
        Assert.NotNull(pedido.Element(ns + "infPedReg"));
        Assert.NotNull(pedido.Element(XNamespace.Get("http://www.w3.org/2000/09/xmldsig#") + "Signature"));
    }

    [Fact]
    public void TratarRespostaCancelamento_SucessoQuandoHaDataRecebimentoSemErros()
    {
        var retorno = new RetornoCancelar();
        var xml = "<CancelarNFSeResposta xmlns=\"" + FiorilliNacionalCancelamento.NamespaceFiorilliNacional + "\"><DataRecebimento>2026-09-04T10:31:00-04:00</DataRecebimento><status>Cancelada</status></CancelarNFSeResposta>";

        FiorilliNacionalCancelamento.TratarRespostaCancelamento(xml, retorno);

        Assert.True(retorno.Sucesso);
        Assert.Empty(retorno.Erros);
        Assert.NotEqual(DateTime.MinValue, retorno.Data);
        Assert.Equal(2026, retorno.Data.Year);
    }

    [Fact]
    public void TratarRespostaCancelamento_ErroQuandoMensagemDeErro()
    {
        var retorno = new RetornoCancelar();
        var xml = RespostaCancelamento(("E0001", "Chave de acesso inválida"), ("A0010", "Aviso qualquer"));

        FiorilliNacionalCancelamento.TratarRespostaCancelamento(xml, retorno);

        Assert.False(retorno.Sucesso);
        Assert.Single(retorno.Erros);
        Assert.Equal("E0001", retorno.Erros[0].Codigo);
        Assert.Single(retorno.Alertas);
        Assert.Equal("A0010", retorno.Alertas[0].Codigo);
    }

    [Theory]
    [InlineData("E0840", "NFS-e já possui evento de cancelamento")]
    [InlineData("E9999", "A NFS-e já está cancelada")]
    public void TratarRespostaCancelamento_JaCanceladaViraSucessoComAlerta(string codigo, string mensagem)
    {
        var retorno = new RetornoCancelar();
        var xml = RespostaCancelamento((codigo, mensagem));

        FiorilliNacionalCancelamento.TratarRespostaCancelamento(xml, retorno);

        Assert.True(retorno.Sucesso);
        Assert.Empty(retorno.Erros);
        Assert.Contains(retorno.Alertas, x => x.Codigo == FiorilliNacionalCancelamento.CodigoJaCancelada);
    }

    [Fact]
    public void TratarRespostaCancelamento_ErroQuandoStatusIndicaRejeicao()
    {
        var retorno = new RetornoCancelar();
        var xml = "<CancelarNFSeResposta xmlns=\"" + FiorilliNacionalCancelamento.NamespaceFiorilliNacional + "\"><DataRecebimento>2026-09-04T10:31:00-04:00</DataRecebimento><status>Rejeitado</status></CancelarNFSeResposta>";

        FiorilliNacionalCancelamento.TratarRespostaCancelamento(xml, retorno);

        Assert.False(retorno.Sucesso);
        Assert.Single(retorno.Erros);
    }

    [Theory]
    [InlineData("000123", "123", true)]
    [InlineData("123", "124", false)]
    [InlineData(null, "123", false)]
    [InlineData("", "", false)]
    public void NumeroIgual_ComparaIgnorandoZerosAEsquerda(string? a, string b, bool esperado)
    {
        Assert.Equal(esperado, FiorilliNacionalCancelamento.NumeroIgual(a, b));
    }

    private static string RespostaConsulta(params (string Numero, string Chave)[] notas)
    {
        var nac = FiorilliNacionalCancelamento.NamespaceFiorilliNacional;
        var ns = FiorilliNacionalCancelamento.NamespaceNFSeNacional;
        var itens = string.Concat(notas.Select(n =>
            "<NFSe versao=\"1.01\" xmlns=\"" + ns + "\"><infNFSe Id=\"NFS" + n.Chave + "\"><nNFSe>" + n.Numero + "</nNFSe></infNFSe></NFSe>"));

        return "<ConsultarNfseResposta xmlns=\"" + nac + "\"><ListaNFSe>" + itens + "</ListaNFSe></ConsultarNfseResposta>";
    }

    private static string RespostaCancelamento(params (string Codigo, string Mensagem)[] mensagens)
    {
        var nac = FiorilliNacionalCancelamento.NamespaceFiorilliNacional;
        var itens = string.Concat(mensagens.Select(m =>
            "<mensagem><Codigo>" + m.Codigo + "</Codigo><Mensagem>" + m.Mensagem + "</Mensagem></mensagem>"));

        return "<CancelarNFSeResposta xmlns=\"" + nac + "\"><DataRecebimento>2026-09-04T10:31:00-04:00</DataRecebimento><ListaMensagens>" + itens + "</ListaMensagens></CancelarNFSeResposta>";
    }
}
