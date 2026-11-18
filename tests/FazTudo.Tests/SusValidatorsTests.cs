using FazTudo.Modules.SiaSus.Services;

namespace FazTudo.Tests;

public class SusValidatorsTests
{
    [Theory]
    [InlineData("100000000000007")]    // definitivo (1/2): PIS 10000000000 + 000 + DV 7
    [InlineData("700000000000005")]    // provisório (7/8/9): soma ponderada múltipla de 11
    [InlineData("700 0000 0000 0005")] // com espaços
    public void Cns_valido(string cns) => Assert.True(SusValidators.IsValidCns(cns));

    [Theory]
    [InlineData("100000000000008")]
    [InlineData("700000000000006")]
    [InlineData("300000000000003")] // faixa inexistente
    [InlineData("777777777777777")] // todos iguais
    [InlineData("12345")]
    [InlineData("")]
    [InlineData(null)]
    public void Cns_invalido(string? cns) => Assert.False(SusValidators.IsValidCns(cns));

    [Theory]
    [InlineData("529.982.247-25")]
    [InlineData("52998224725")]
    public void Cpf_valido(string cpf) => Assert.True(SusValidators.IsValidCpf(cpf));

    [Theory]
    [InlineData("529.982.247-24")]
    [InlineData("111.111.111-11")]
    [InlineData("123")]
    [InlineData(null)]
    public void Cpf_invalido(string? cpf) => Assert.False(SusValidators.IsValidCpf(cpf));

    [Fact]
    public void Normaliza_codigo_de_procedimento() =>
        Assert.Equal("0204030188", SusValidators.NormalizeProcedureCode("02.04.03.018-8"));
}
