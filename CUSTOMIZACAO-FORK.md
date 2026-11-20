# Customizando o Reversa no fork

Guia para incluir skills próprias neste fork (`flaviogarrido/reversa`), testar, entregar aos projetos e continuar acompanhando o upstream (`sandeco/reversa`).

> **Resumo:** criar uma skill é só uma pasta em `agents/`. O fork é entregue pelo pacote `@flaviogarrido/reversa` no npm (seção 3), e não pelo `reversa` original.

---

## 1. Como criar uma skill

> **Atalho:** o [Skill Studio](tools/SkillStudio/README.md) (app WPF em `tools/SkillStudio/`) faz tudo desta seção por você. Ele gera os dois arquivos em lockstep, valida as regras abaixo (64 no total) enquanto você escreve, bloqueia o salvamento com erro e traz um assistente com a API do Claude que entrevista você e busca skills parecidas. A aba **Playground** roda casos de teste da skill contra o Claude e mostra um placar de confiança; os casos ficam em `evals/<skill>/`, fora do pacote. O botão **Documentar** do assistente escreve a página da skill no site (seção *Passos opcionais*). O workflow `skill-studio.yml` aplica a mesma validação no CI.

O instalador copia **toda pasta que existir em `agents/`** para o projeto do usuário. Quem faz isso é `listAllAgents()` em `lib/installer/prompts.js`, que lê o diretório inteiro. Não existe registro manual.

### Estrutura mínima

```
agents/fg-minha-skill/
├── SKILL.md              # obrigatório
├── agents/openai.yaml    # obrigatório (o verificador exige)
└── references/           # opcional: templates, checklists, exemplos
```

### `SKILL.md`

Segue o mesmo padrão das skills existentes (por exemplo, `agents/reversa-add/SKILL.md`):

```yaml
---
name: fg-minha-skill
description: 'Resumo humano do que a skill faz e quando usar.'
disable-model-invocation: true   # true = só roda via /fg-minha-skill; omita = o modelo pode acionar sozinho
license: MIT
compatibility: Claude Code, Codex, Cursor, Gemini CLI e demais agentes compatíveis com Agent Skills.
metadata:
  author: flaviogarrido
  version: "1.0.0"
  framework: reversa
---

Instruções do agente...
```

### `agents/openai.yaml`

Precisa estar em *lockstep* com o frontmatter do `SKILL.md`, ou seja, as duas marcas dizem a mesma coisa:

```yaml
interface:
  display_name: "FG Minha Skill"
  short_description: "Resumo curto"
policy:
  allow_implicit_invocation: false   # false se disable-model-invocation: true
```

### Regras do verificador (`scripts/verify-invocation.py`)

A skill é reprovada se:

- só um dos arquivos a marca como *user-invoked* (`disable-model-invocation: true` no `SKILL.md` sem `allow_implicit_invocation: false` no `openai.yaml`, ou o contrário);
- `openai.yaml` não existe ou não tem `interface.display_name` ou `interface.short_description`;
- a `description` de uma skill *user-invoked* contém frases-gatilho como `Ative com `, `digitar "`, `Use com "` ou `pedir "`. Ela deve ser um resumo para humanos, não uma lista de gatilhos.

### Passos opcionais

- **Time no resumo do install:** inclua o ID numa das listas de `lib/installer/prompts.js` (`FORWARD_TEAM`, `DOCS_TEAM` etc.). Isso só muda o resumo exibido pelo instalador. Uma skill fora de qualquer lista aparece como "Discovery Team".
- **Descoberta:** cite a skill em `agents/reversa-agents-help/SKILL.md`.
- **Página no site:** crie `docs/agentes/<skill>.md`, `.pt.md` e `.es.md`, inclua a página no grupo *Fork Agents* do nav em `mkdocs.yml` e acrescente uma linha na tabela "Agentes do fork" de `docs/agentes/index*.md`. O Skill Studio faz isso pelo botão **Documentar**, e os testes dele exigem a página completa para toda skill `fg-*`.

### Use um prefixo próprio

Use `fg-` (ou outro prefixo seu) em vez de `reversa-`. Isso traz três vantagens:

1. **Não gera conflito de merge** com o upstream, porque suas pastas nunca existem lá.
2. **Deixa claro** o que é do fork e o que é original.
3. **Não depende do conserto do `update`.** No upstream, a checagem "arquivo modificado pelo usuário" de `lib/commands/update.js` usa `startsWith(relDir)` sem a `/` final, e editar uma skill `reversa-xyz` bloqueia a atualização da `reversa`. O fork já corrige isso (seção 3), mas quem instalar pelo pacote original continua com o bug.

---

## 2. Testar localmente antes de publicar

Num projeto descartável, instale direto do checkout do fork:

```bash
node C:/repos/reversa/bin/reversa.js install
```

No repositório do Reversa, rode as verificações. No Windows, o verificador em Python precisa de UTF-8 no console:

```powershell
$env:PYTHONIOENCODING="utf-8"; python scripts/verify-invocation.py; node scripts/test-installer-transport.mjs
```

Confira se a skill nova entra no pacote publicado:

```bash
npm pack --dry-run
```

O workflow `.github/workflows/verify-invocation.yml` roda o mesmo verificador no CI do fork a cada push ou PR que mexa em `agents/**`.

> Observação: `npm run verify` chama `python3`, que no Windows costuma abrir o atalho da Microsoft Store. Fora do CI (Linux), prefira o comando acima.

---

## 3. Entrega: pacote do fork no npm

O fork é publicado como **`@flaviogarrido/reversa`**. O comando continua sendo `reversa`; muda só o que você digita no `npx`:

```bash
npx @flaviogarrido/reversa install
```

```bash
npx @flaviogarrido/reversa update
```

### O que foi ajustado para o fork funcionar

- `package.json`: nome com escopo, `publishConfig.access = public` e links apontando para este repositório.
- `lib/utils/package-info.js`: lê nome e versão do pacote. Tudo que fala com o npm ou sugere comandos ao usuário usa esse nome, e não o literal `reversa`.
- `lib/commands/update.js`:
  - consulta o `latest` do **próprio** pacote no npm;
  - grava no projeto a versão do pacote **em execução**, mesmo offline (antes gravava a do npm, e offline não gravava nada);
  - registra a origem em `.reversa/state.json` (`"package"`). Um projeto instalado pelo `reversa` original é sempre atualizado pelo fork, mesmo que a versão instalada pareça maior ou igual, porque as numerações não são comparáveis;
  - compara pastas de skill com a `/` final, por `isInsideDir()` em `lib/installer/manifest.js`. Antes, editar `reversa-docs/` bloqueava a atualização de `reversa/`.
- `lib/commands/install.js` e `lib/installer/writer.js`: a instalação já grava a origem (`"package"`) em `.reversa/state.json`, junto com a versão instalada.
- `scripts/test-fork-delivery.mjs`: testa tudo isso num projeto temporário que simula uma instalação do upstream. Faz parte do `npm run verify`.

### Migrar um projeto que usa o Reversa original

Na raiz do projeto, rode o update pelo pacote do fork:

```bash
npx @flaviogarrido/reversa update
```

Os arquivos que você editou são preservados (manifesto SHA-256 em `.reversa/_config/files-manifest.json`). As skills novas do fork, como a `fg-backlog`, são instaladas, e `.reversa/state.json` passa a registrar `"package": "@flaviogarrido/reversa"`.

### Publicar uma versão

A publicação é feita pelo workflow `.github/workflows/publish.yml`, disparado por tag. Ele confere se a tag bate com o `package.json`, roda os gates (`npm run verify` e os testes do Skill Studio), mostra o conteúdo do pacote e publica com proveniência.

**Configuração (uma vez só):**

1. No npmjs.com, crie a conta ou organização dona do escopo `@flaviogarrido`.
2. O Trusted Publisher só pode ser configurado num pacote que já existe. Se o pacote ainda não estiver no npm, faça a primeira publicação da sua máquina, com `npm publish --access public`.
3. Em *pacote → Settings → Trusted Publisher*, configure o GitHub Actions com o repositório `flaviogarrido/reversa` e o workflow `publish.yml`, com o campo Environment vazio. Em *Allowed actions*, marque **Allow npm publish**. Sem essa opção o trusted publisher só faz `npm stage publish`, e o workflow falha com `ENEEDAUTH`.

O workflow autentica só por OIDC (npm ≥ 11.5.1, garantido no próprio workflow). Não há token nem segredo de npm no repositório.

**A cada release:**

```bash
npm version patch
```

```bash
git push
```

```bash
git push origin v1.3.9
```

O `npm version` sobe a versão, faz o commit e cria a tag `vX.Y.Z` (troque `v1.3.9` pela versão que ele mostrou). O push da tag dispara a publicação.

Envie a tag sozinha, não com `git push --follow-tags`. O `--follow-tags` também leva as tags anotadas do upstream que ainda não estão no fork, e o GitHub não dispara workflows quando um push leva mais de 3 tags de uma vez. Foi o que travou a 1.3.8. Se uma tag chegou ao GitHub e a publicação não rodou, dispare-a à mão:

```bash
gh workflow run publish.yml --repo flaviogarrido/reversa --ref v1.3.9
```

### Alternativa: instalar direto do GitHub (sem npm)

```bash
npx github:flaviogarrido/reversa#v1.3.9 install
```

Fixe uma tag (`#vX.Y.Z`) para ter builds reproduzíveis. Nesse modo o `update` não encontra o pacote no npm, segue offline e instala a versão da tag usada.

---

## 4. Acompanhar o upstream

Configure o remoto do projeto original (uma vez só):

```bash
git remote add upstream https://github.com/sandeco/reversa.git
```

Rotina de sincronização:

```bash
git fetch upstream
```

```bash
git merge upstream/main
```

Se as suas skills ficarem em pastas `fg-*` e você mexer pouco em `lib/`, os conflitos ficam praticamente restritos a `package.json` (nome e versão). Depois do merge:

1. Resolva o `package.json`: mantenha o seu `name` e use uma versão **maior** que a sua anterior e que a do upstream.
2. Rode as verificações da seção 2.
3. Siga a rotina de release.

### Observações

- **Workflows agendados em forks** ficam desativados até você ativá-los na aba *Actions* do GitHub. Isso vale para `sync-impeccable.yml`, que atualiza o Impeccable diariamente e abre PR.
- **Sincronização automática:** dá para criar um workflow parecido com `sync-impeccable.yml` que, num `schedule`, faz `git fetch upstream` e abre PR com `peter-evans/create-pull-request` quando houver commits novos em `upstream/main`.
- **Mudanças em `lib/`** (como a correção do bug do `update`) podem virar PR para o upstream. Quanto menos o fork divergir em `lib/`, mais barata fica a sincronização.

---

## Checklist rápido

- [ ] Skill em `agents/fg-<nome>/` com `SKILL.md` e `agents/openai.yaml` em lockstep
- [ ] `description` sem frases-gatilho (se for *user-invoked*)
- [ ] `npm run verify` aprovado (inclui `test-fork-delivery.mjs`)
- [ ] Instalação testada num projeto descartável
- [ ] `npm pack --dry-run` lista a skill
- [ ] Página em `docs/agentes/` nos três idiomas e no menu do site
- [x] `package.json` com nome com escopo e `update.js` usando o nome do pacote
- [x] Trusted Publisher configurado no pacote do npm, com *Allow npm publish*
- [ ] `npm version patch`, `git push` e `git push origin vX.Y.Z` (o workflow publica)
- [ ] Remoto `upstream` configurado e sincronizado periodicamente
