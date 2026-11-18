# Conserta SIA · CPD — faturamento SIA/SUS sem glosa

Ferramenta para quem fecha a produção ambulatorial do SUS: lê o relatório de erros do SIA e diz, problema por
problema, **onde se resolve** (BPA, APAC, CNES, FPO ou no próprio SIA), **para qual estabelecimento mandar** e
**como corrigir**.

## O que tem

| Ferramenta | O que faz | Web | Desktop |
|---|---|:-:|:-:|
| **Solucionador SIA** | Lê o `R0350.OUT` (ocorrências da consistência) e explica cada ocorrência, cruzando com as regras do SIGTAP | ✓ | ✓ |
| **Base de Erros** | Erros de faturamento com causa, impacto e passo a passo | ✓ | ✓ |
| **Consultor SIGTAP** | Consulta de procedimentos (instrumento, CBO, sexo, idade, CID, valor); importa o zip oficial | ✓ | ✓ |
| **Triador BPA** | Acha, antes do envio, as linhas da remessa do BPA-Mag que o SIA vai rejeitar; exporta para Excel | ✓ | ✓ |
| **Doutor TI DATASUS** | Checa o Windows: VirtualStore, Firebird 3050, midas.dll, BDE | — | ✓ |

## Estrutura

- `web/` — app **Streamlit** (Python). Arquivo principal: `web/app.py`.
- `src/` — programa **desktop** para Windows (C#/.NET 8, WPF).
- `shared/` — base de erros e base de exemplo do SIGTAP, lidas pelas duas versões. **Edite a base de erros aqui.**
- `samples/` — arquivos de exemplo (fictícios ou com documentos trocados por números falsos).
- `tests/` e `web/tests/` — testes das duas versões, com os mesmos arquivos de exemplo.

## Rodar o app web

```powershell
python -m venv web\.venv
web\.venv\Scripts\pip install -r web\requirements.txt
web\.venv\Scripts\streamlit run web\app.py
```

## Tabelas SIGTAP

O site procura tabelas novas no FTP do DATASUS a cada 6 horas e **baixa sozinho**, mas **só troca a tabela em uso
quando alguém escolhe** (Consultor SIGTAP → *Usar esta*). A escolha vale para todos que usam o site.

Quando o servidor reinicia (o Streamlit Cloud reinicia ao "acordar"), vale a **tabela padrão do projeto**, gravada em
`shared/sigtap/`. Para trocar o padrão (baixa do DATASUS e grava):

```powershell
web\.venv\Scripts\python web\tools\preparar_sigtap.py 202612
```

Depois, commit + push da pasta `shared/sigtap/`. Para exigir senha na troca da tabela em uso, cadastre `senha_cpd` nos
*Secrets* do app no Streamlit Cloud.

## Privacidade

Os arquivos enviados ao app web (relatório do SIA, remessa BPA, zip do SIGTAP) são lidos só em memória durante a sessão
e não são gravados. Mesmo assim, a remessa BPA tem dados de pacientes: confirme com a Secretaria se o uso online é
permitido. O programa desktop processa tudo no próprio computador.

## Testes

```powershell
dotnet test
web\.venv\Scripts\python -m pytest web
```
