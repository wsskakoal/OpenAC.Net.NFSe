// ***********************************************************************
// Assembly         : OpenAC.Net.NFSe
// Author           : Rafael Dias
// Created          : 29-01-2020
//
// Last Modified By : Rafael Dias
// Last Modified On : 29-01-2020
// ***********************************************************************
// <copyright file="ProviderFiorilli.cs" company="OpenAC .Net">
//		        		   The MIT License (MIT)
//	     		Copyright (c) 2014 - 2024 Projeto OpenAC .Net
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

using OpenAC.Net.NFSe.Configuracao;
using System.Xml.Linq;
using OpenAC.Net.DFe.Core;
using OpenAC.Net.DFe.Core.Common;
using OpenAC.Net.NFSe.Commom;
using OpenAC.Net.NFSe.Commom.Interface;
using OpenAC.Net.NFSe.Commom.Model;
using OpenAC.Net.NFSe.Commom.Types;
using OpenAC.Net.NFSe.Nota;

namespace OpenAC.Net.NFSe.Providers;

internal sealed class ProviderFiorilli200 : ProviderABRASF200
{
    #region Constructors

    public ProviderFiorilli200(ConfigNFSe config, OpenMunicipioNFSe municipio) : base(config, municipio)
    {
        Name = "Fiorilli";
    }

    #endregion Constructors

    #region Methods

    protected override XElement? WriteTomadorRps(NotaServico nota)
    {
        if (nota.Tomador.Endereco.CodigoMunicipio != 9999999)
            nota.Tomador.Endereco.CodigoPais = 0;

        return base.WriteTomadorRps(nota);
    }

    protected override IServiceClient GetClient(TipoUrl tipo) => new Fiorilli200ServiceClient(this, tipo);

    protected override void AssinarEnviar(RetornoEnviar retornoWebservice)
    {
        if(Configuracoes.WebServices.Ambiente == DFeTipoAmbiente.Producao)
            base.AssinarEnviar(retornoWebservice);
        else
            retornoWebservice.XmlEnvio = XmlSigning.AssinarXmlTodos(retornoWebservice.XmlEnvio, "Rps", "", Certificado);
    }

    protected override void AssinarEnviarSincrono(RetornoEnviar retornoWebservice)
    {
        if(Configuracoes.WebServices.Ambiente == DFeTipoAmbiente.Producao)
            base.AssinarEnviarSincrono(retornoWebservice);
        else
            retornoWebservice.XmlEnvio = XmlSigning.AssinarXmlTodos(retornoWebservice.XmlEnvio, "Rps", "", Certificado);
    }
    
    protected override void AssinarCancelarNFSe(RetornoCancelar retornoWebservice)
    {
        if(Configuracoes.WebServices.Ambiente == DFeTipoAmbiente.Producao)
            base.AssinarCancelarNFSe(retornoWebservice);
    }
    
    protected override void AssinarSubstituirNFSe(RetornoSubstituirNFSe retornoWebservice)
    {
        if(Configuracoes.WebServices.Ambiente == DFeTipoAmbiente.Producao)
            base.AssinarSubstituirNFSe(retornoWebservice);
    }
    
    /// <summary>
    /// O aviso "FI410 - serviço em desativação" que a Fiorilli devolve nas operações ABRASF vira alerta,
    /// e não erro: a resposta pode trazer os dados/confirmação junto do aviso, e o parser precisa lê-los.
    /// </summary>
    protected override void MensagemErro(RetornoWebservice retornoWs, XContainer xmlRet, string xmlTag)
    {
        base.MensagemErro(retornoWs, xmlRet, xmlTag);
        FiorilliNacionalCancelamento.ReclassificarAvisosDesativacao(retornoWs);
    }

    /// <summary>
    /// Cancela pelo ABRASF e, se a prefeitura recusar por desativação do serviço (FI410), cancela pelo
    /// webservice da Fiorilli no layout nacional (ver <see cref="FiorilliNacionalCancelamento"/>).
    /// </summary>
    public override RetornoCancelar CancelarNFSe(string codigoCancelamento, string numeroNFSe, string serieNFSe, decimal valorNFSe, string motivo, string? codigoVerificacao, NotaServicoCollection notas)
    {
        var retorno = base.CancelarNFSe(codigoCancelamento, numeroNFSe, serieNFSe, valorNFSe, motivo, codigoVerificacao, notas);
        if (retorno.Sucesso || !FiorilliNacionalCancelamento.ContemAvisoDesativacao(retorno)) return retorno;

        FiorilliNacionalCancelamento.CancelarLayoutNacional(this, retorno, notas);
        return retorno;
    }

    #endregion Methods
}