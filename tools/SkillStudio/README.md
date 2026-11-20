# Reversa Skill Studio

App desktop (.NET 10, WPF) para criar e editar as skills do seu fork do Reversa. Ele valida a skill enquanto você escreve, antes de qualquer commit, e traz um assistente com a API do Claude que entrevista você e busca skills parecidas para usar como referência.

O app fica fora do pacote npm: o `package.json` só publica `agents/`, `bin/`, `lib/` e `templates/`.

## Requisitos

- Windows 10 ou 11 com o SDK do .NET 10
- Python 3 e Node 18+ no PATH, só para o botão **Verificar repositório**
- Microsoft Edge WebView2 Runtime, só para a visualização do Markdown (já vem no Windows 11)
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
| **Instruções** | Corpo do SKILL.md num editor Markdown com numeração de linhas. **Editar**, **Dividir** e **Visualizar** alternam entre o texto, o texto ao lado do Markdown formatado e só o formatado. |
| **Referências** | Arquivos de `references/`, `scripts/` e `assets/`. Binários são preservados sem edição. Arquivos `.md` têm os mesmos modos de visualização das Instruções. |
| **Metadados** | Autor, versão, framework, licença, compatibilidade, time, fase, papel e etapa. |
| **Arquivos gerados** | O SKILL.md e o `agents/openai.yaml` exatamente como serão gravados, mais o resultado da verificação do repositório. |
| **Playground** | Casos de teste da skill, execução contra o Claude e o placar de confiança. Veja [Playground](#playground). |
| **Regras** | Catálogo completo das regras de validação. |
| **Validação** (embaixo) | Erros, avisos e sugestões ao vivo. Duplo clique leva ao campo do problema. |

Ao salvar, o app grava `agents/<nome>/` com UTF-8 sem BOM e finais de linha LF, como manda o `.gitattributes`. Se nada mudou, nada é gravado. Arquivos de texto removidos no editor são apagados do disco; binários nunca são tocados. Renomear uma skill cria uma pasta nova e mantém a antiga.

O botão **Salvar** fica bloqueado enquanto houver erro.

## Regras de validação

São 64 regras, em três níveis:

- **Erro**: bloqueia o salvamento.
- **Aviso**: indica risco ou desvio de convenção.
- **Sugestão**: dica de qualidade.

A lista completa está na aba **Regras** e em [RuleCatalog.cs](src/SkillStudio.Core/Validation/RuleCatalog.cs). As principais:

- **Paridade com o CI (RS090):** o editor renderiza os arquivos e aplica um porte fiel do `scripts/verify-invocation.py`. Verifica o lockstep entre `disable-model-invocation` e `allow_implicit_invocation`, a presença de `display_name` e `short_description`, e a ausência de gatilhos de modelo na description de skills user-invoked (RS013).
- **Nome:** kebab-case, até 64 caracteres, nome único, sem as palavras reservadas `claude` e `anthropic`, sem o prefixo `reversa-` do upstream. A regra RS005 avisa quando o nome é prefixo de outra skill, que é o bug do `reversa update` (`startsWith` sem a `/` final).
- **Ida e volta pelo YAML (RS091):** o que é gravado precisa ser lido de volta igual, o que pega erros de aspas, dois-pontos e escape.
- **Referências (RS053):** todo `references/...` citado no corpo precisa existir. Se o arquivo citado só existe em outra skill, o aviso diz isso.
- **Corpo:** blocos de código fechados, sem `---` no início, até 500 linhas, sem TODO e sem caminhos absolutos da sua máquina.
- **Testes (RS100–RS108):** só aparecem no editor, nunca são erro e nunca bloqueiam o salvamento. Avisam quando faltam casos de gatilho negativos, quando um caso de comportamento não tem asserção e quando a última execução está desatualizada ou teve falhas.
- **Documentação (RS110–RS112):** também só no editor e só para skills com o prefixo do fork. Avisam quando a skill não tem página em `docs/agentes/`, quando falta algum dos três idiomas ou quando a página não está no menu do site.
- **Segurança:** bloqueia chaves de API, tokens e chaves privadas (RS070). Avisa sobre comandos destrutivos e lembra a política de edição do legado (`.reversa/reversa-config.json`).

As regras foram calibradas contra as 78 skills do upstream: nenhuma delas gera erro.

## Assistente (Claude)

O painel da direita conversa com a API do Claude pelo SDK oficial em C# (`Anthropic`).

- **Iniciar entrevista:** o assistente faz poucas perguntas por vez sobre objetivo, gatilho, entradas, passos, saídas e limites.
- **Busca de referências:** assim que entende o objetivo, ele busca as skills parecidas no repositório e lê as mais próximas para seguir a mesma estrutura e as mesmas convenções. Com a busca web ligada, também traz referências externas, com a fonte.
- **Preenchimento:** depois que você confirma o resumo, ele preenche o editor pela ferramenta `update_draft`. A resposta da ferramenta traz o resultado da validação, e o assistente corrige os erros antes de encerrar.
- **Revisar rascunho:** pede uma revisão do que já está no editor.
- **Documentar:** escreve a página da skill no site de documentação (veja abaixo).
- **Nada vai para `agents/` sem o seu clique em Salvar.** A exceção é a documentação, que fica fora da skill.

Ferramentas expostas ao modelo: `search_similar_skills`, `read_skill`, `get_current_draft`, `update_draft`, `get_test_cases`, `update_test_cases`, `get_docs_pages`, `update_docs_pages` e, opcionalmente, `web_search`.

Configuração padrão (em **Configurações**):

- Modelo `claude-opus-5-5` com effort `medium`.
- Fallback de recusa no servidor (`fallbacks: "default"`).
- Cache no prompt de sistema.

**Credencial**, na ordem em que é procurada:

1. Chave salva no app, cifrada com DPAPI no seu usuário do Windows.
2. Variável `ANTHROPIC_API_KEY` ou `ANTHROPIC_AUTH_TOKEN`.
3. Perfil do `ant auth login`.

## Playground

O Playground aumenta a confiança numa skill nova antes do commit: você descreve casos de teste, roda contra o Claude e lê um placar.

**Dois tipos de caso:**

| Tipo | O que mede | Como roda |
|---|---|---|
| **Gatilho** | Se o modelo escolhe esta skill para o pedido. Só para skills model-invoked. | O modelo recebe o catálogo das skills model-invoked do repositório (com a description atual do editor) e uma ferramenta `Skill` para chamar uma delas. Casos **positivos** devem acionar a skill; **negativos** são pedidos vizinhos que não devem. |
| **Comportamento** | Se a resposta com a skill carregada está certa. | O corpo da skill vai no prompt de sistema, com uma ferramenta `read_skill_file` para os arquivos de `references/`. O teste não tem acesso ao disco do projeto: cole no pedido o contexto que a skill precisaria ler. |

**Asserções** dos casos de comportamento: *Contém*, *Não contém*, *Regex* (checagens locais) e *Rubrica*, um critério em linguagem natural julgado pelo Claude com esforço baixo e saída estruturada.

**Execução:**

- Cada caso roda N vezes (padrão 2). Um caso que passa só em parte das repetições é marcado como **instável** e conta como falha.
- **Comparar sem a skill** roda os casos de comportamento também sem ela. Se o modelo já passa sem a skill, os casos não estão medindo o que ela acrescenta.
- Antes de rodar, o app mostra quantas chamadas à API serão feitas e pede confirmação.
- **Que falharam** roda só os casos que falharam ou mudaram, aproveitando o resto da última execução se a skill não mudou.

**Placar de confiança** (0 a 100): comportamento vale 60 e gatilho 40 (skill user-invoked: só comportamento). Alta a partir de 90, média a partir de 70. O nível é limitado quando há erro de validação, resultado desatualizado, menos de 3 casos de comportamento, menos de 2 gatilhos positivos e 2 negativos, só 1 repetição, casos instáveis ou casos que não rodaram. Cada limite aparece como motivo no placar.

**Com o assistente:** *Propor casos* pede ao Claude que leia a skill e grave casos pelo `update_test_cases`. *Analisar falhas* envia as falhas e as respostas para ele dizer se o problema está na skill ou no caso.

**Onde ficam:** `evals/<skill>/cases.json` (casos, versionados) e `evals/<skill>/last-run.json` (última execução, com as respostas truncadas). A pasta fica fora de `agents/`, então não vai para os projetos pelo instalador nem para o pacote npm. Os dois arquivos são gravados junto com a skill, no **Salvar**. A suíte de testes do Skill Studio confere que todo `cases.json` é válido e aponta para uma skill que existe.

## Página de documentação

O site do Reversa (`docs/`, publicado com MkDocs) tem uma página por agente em `docs/agentes/`, em três idiomas: `<skill>.md` (inglês, o padrão), `<skill>.pt.md` e `<skill>.es.md`. Skills novas do fork não ganham página sozinhas.

O botão **Documentar** do assistente resolve isso:

1. O assistente lê a skill aberta e uma página existente como modelo (`get_docs_pages`). A skill precisa estar salva, porque a página usa o nome da pasta.
2. Escreve a página nos três idiomas, no formato das outras: comando e fase, uma analogia, o que faz, o que produz e quando usar. O conteúdo vem só do corpo e das referências da skill.
3. Grava com `update_docs_pages`. Na primeira vez, também registra a página no menu do `mkdocs.yml` (grupo **Fork Agents**, com as traduções) e acrescenta uma linha na tabela "Agentes do fork" do índice de agentes, nos idiomas em que essa seção existe.

Diferente do rascunho da skill, a documentação vai direto para o disco. Uma página que já existe só é substituída com a sua confirmação. Os arquivos entram no mesmo commit da skill.

A suíte de testes confere que toda skill `fg-*` do repositório tem a página completa e está no menu.

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
- O Playground com um modelo falso: gatilho, asserções, linha de base, instabilidade, execução parcial, placar de confiança, persistência em `evals/` e as ferramentas de casos do assistente.
- Regressão contra todas as skills do repositório: nenhuma pode ter erro, e a maioria é regravada byte a byte.

O workflow [.github/workflows/skill-studio.yml](../../.github/workflows/skill-studio.yml) roda essa suíte a cada mudança em `agents/**`, `evals/**`, `docs/agentes/**` ou `mkdocs.yml`. Uma skill com erro reprova o CI mesmo que tenha sido editada fora do app.

Existe também um teste de ponta a ponta contra a API real. Ele só roda se você pedir, porque consome tokens:

```powershell
$env:SKILLSTUDIO_LIVE = "1"; dotnet test tools/SkillStudio/tests/SkillStudio.Tests --filter LiveAssistant
```
