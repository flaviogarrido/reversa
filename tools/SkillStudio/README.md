# Reversa Skill Studio

App desktop (.NET 10, WPF) para criar e editar as skills do seu fork do Reversa. Ele valida a skill enquanto você escreve, antes de qualquer commit, e traz um assistente com a API do Claude que entrevista você e busca skills parecidas para usar como referência.

O app fica fora do pacote npm: o `package.json` só publica `agents/`, `bin/`, `lib/` e `templates/`.

## Requisitos

- Windows 10 ou 11 com o SDK do .NET 10
- Python 3 e Node 18+ no PATH, só para o botão **Verificar repositório**
- Uma credencial da API do Claude, só para o assistente (veja [Assistente](#assistente-claude))

## Como rodar

Na raiz do repositório:

```bash
dotnet run --project tools/SkillStudio/src/SkillStudio.App
```

O app localiza sozinho o checkout do Reversa subindo a partir da pasta do executável. Para usar outro checkout, clique em **Repositório**.

Para abrir uma skill direto pela linha de comando, passe o nome dela. O segundo argumento, opcional, é o índice da aba:

```bash
dotnet run --project tools/SkillStudio/src/SkillStudio.App -- fg-minha-skill 1
```

## O que o editor faz

| Área | Conteúdo |
|---|---|
| **Skills** (esquerda) | Todas as pastas de `agents/`, com filtro e as marcas user/model, upstream/fork e time. Duplo clique abre a skill. Com o botão direito, **Usar como modelo** copia a estrutura de uma skill para uma nova. |
| **Identidade** | Nome, modo de invocação, description com contador, `display_name` e `short_description` gerados automaticamente no padrão do repositório, e as skills mais parecidas (TF-IDF local, atualizada enquanto você digita). |
| **Instruções** | Corpo do SKILL.md num editor Markdown com numeração de linhas. |
| **Referências** | Arquivos de `references/`, `scripts/` e `assets/`. Binários são preservados sem edição. |
| **Metadados** | Autor, versão, framework, licença, compatibilidade, time, fase, papel e etapa. |
| **Arquivos gerados** | O SKILL.md e o `agents/openai.yaml` exatamente como serão gravados, mais o resultado da verificação do repositório. |
| **Regras** | Catálogo completo das regras de validação. |
| **Validação** (embaixo) | Erros, avisos e sugestões ao vivo. Duplo clique leva ao campo do problema. |

Ao salvar, o app grava `agents/<nome>/` com UTF-8 sem BOM e finais de linha LF, como manda o `.gitattributes`. Se nada mudou, nada é gravado. Arquivos de texto removidos no editor são apagados do disco; binários nunca são tocados. Renomear uma skill cria uma pasta nova e mantém a antiga.

O botão **Salvar** fica bloqueado enquanto houver erro.

## Regras de validação

São 52 regras, em três níveis:

- **Erro**: bloqueia o salvamento.
- **Aviso**: indica risco ou desvio de convenção.
- **Sugestão**: dica de qualidade.

A lista completa está na aba **Regras** e em [RuleCatalog.cs](src/SkillStudio.Core/Validation/RuleCatalog.cs). As principais:

- **Paridade com o CI (RS090):** o editor renderiza os arquivos e aplica um porte fiel do `scripts/verify-invocation.py`. Verifica o lockstep entre `disable-model-invocation` e `allow_implicit_invocation`, a presença de `display_name` e `short_description`, e a ausência de gatilhos de modelo na description de skills user-invoked (RS013).
- **Nome:** kebab-case, até 64 caracteres, nome único, sem as palavras reservadas `claude` e `anthropic`, sem o prefixo `reversa-` do upstream. A regra RS005 avisa quando o nome é prefixo de outra skill, que é o bug do `reversa update` (`startsWith` sem a `/` final).
- **Ida e volta pelo YAML (RS091):** o que é gravado precisa ser lido de volta igual, o que pega erros de aspas, dois-pontos e escape.
- **Referências (RS053):** todo `references/...` citado no corpo precisa existir. Se o arquivo citado só existe em outra skill, o aviso diz isso.
- **Corpo:** blocos de código fechados, sem `---` no início, até 500 linhas, sem TODO e sem caminhos absolutos da sua máquina.
- **Segurança:** bloqueia chaves de API, tokens e chaves privadas (RS070). Avisa sobre comandos destrutivos e lembra a política de edição do legado (`.reversa/reversa-config.json`).

As regras foram calibradas contra as 78 skills do upstream: nenhuma delas gera erro.

## Assistente (Claude)

O painel da direita conversa com a API do Claude pelo SDK oficial em C# (`Anthropic`).

- **Iniciar entrevista:** o assistente faz poucas perguntas por vez sobre objetivo, gatilho, entradas, passos, saídas e limites.
- **Busca de referências:** assim que entende o objetivo, ele busca as skills parecidas no repositório e lê as mais próximas para seguir a mesma estrutura e as mesmas convenções. Com a busca web ligada, também traz referências externas, com a fonte.
- **Preenchimento:** depois que você confirma o resumo, ele preenche o editor pela ferramenta `update_draft`. A resposta da ferramenta traz o resultado da validação, e o assistente corrige os erros antes de encerrar.
- **Revisar rascunho:** pede uma revisão do que já está no editor.
- **Nada vai para o disco sem o seu clique em Salvar.**

Ferramentas expostas ao modelo: `search_similar_skills`, `read_skill`, `get_current_draft`, `update_draft` e, opcionalmente, `web_search`.

Configuração padrão (em **Configurações**):

- Modelo `claude-opus-5-5` com effort `medium`.
- Fallback de recusa no servidor (`fallbacks: "default"`).
- Cache no prompt de sistema.

**Credencial**, na ordem em que é procurada:

1. Chave salva no app, cifrada com DPAPI no seu usuário do Windows.
2. Variável `ANTHROPIC_API_KEY` ou `ANTHROPIC_AUTH_TOKEN`.
3. Perfil do `ant auth login`.

## Estrutura

```
tools/SkillStudio/
├── src/SkillStudio.Core/      # net10.0, sem UI: modelo, serialização, regras, busca, assistente
├── src/SkillStudio.App/       # net10.0-windows, WPF + WPF-UI (Fluent), AvalonEdit, MVVM Toolkit
└── tests/SkillStudio.Tests/   # xUnit
```

## Testes e CI

Rode a suíte local:

```bash
dotnet test tools/SkillStudio/tests/SkillStudio.Tests
```

A suíte cobre:

- Cada regra de validação.
- Ida e volta do YAML com textos difíceis.
- Gravação em disco: LF, sem BOM, binários preservados e bloqueio de caminho fora da pasta.
- O formato de fio do assistente.
- Regressão contra todas as skills do repositório: nenhuma pode ter erro, e a maioria é regravada byte a byte.

O workflow [.github/workflows/skill-studio.yml](../../.github/workflows/skill-studio.yml) roda essa suíte a cada mudança em `agents/**`. Uma skill com erro reprova o CI mesmo que tenha sido editada fora do app.

Existe também um teste de ponta a ponta contra a API real. Ele só roda se você pedir, porque consome tokens:

```powershell
$env:SKILLSTUDIO_LIVE = "1"; dotnet test tools/SkillStudio/tests/SkillStudio.Tests --filter LiveAssistant
```
