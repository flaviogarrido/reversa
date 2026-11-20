# Customizando o Reversa no fork

Guia para incluir skills próprias neste fork (`flaviogarrido/reversa`), testar, entregar aos projetos e continuar acompanhando o upstream (`sandeco/reversa`).

> **Resumo:** criar uma skill é só uma pasta em `agents/`. O fork é entregue pelo pacote `@flaviogarrido/reversa` no npm (seção 3), e não pelo `reversa` original.

---

## 1. Como criar uma skill

> **Atalho:** o [Skill Studio](tools/SkillStudio/README.md) (app WPF em `tools/SkillStudio/`) faz tudo desta seção por você. Ele gera os dois arquivos em lockstep, valida as regras abaixo (52 no total) enquanto você escreve, bloqueia o salvamento com erro e traz um assistente com a API do Claude que entrevista você e busca skills parecidas. O workflow `skill-studio.yml` aplica a mesma validação no CI.

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
- **Descoberta:** cite a skill em `agents/reversa-agents-help/SKILL.md` e em `docs/`.

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
  - compara pastas de skill com a `/` final. Antes, editar `reversa-docs/` bloqueava a atualização de `reversa/`.
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
2. Faça a primeira publicação com um token granular (*Read and write*, com *Bypass 2FA*) guardado no segredo `NPM_TOKEN` do fork, em *Settings → Secrets and variables → Actions*.
3. Com o pacote já no npm, configure em *pacote → Settings → Trusted Publisher* o GitHub Actions com o repositório `flaviogarrido/reversa` e o workflow `publish.yml`. A partir daí o workflow autentica por OIDC (npm ≥ 11.5.1, garantido no próprio workflow), e o token e o segredo `NPM_TOKEN` podem ser apagados.

**A cada release:**

```bash
npm version patch
```

```bash
git push --follow-tags
```

O `npm version` sobe a versão, faz o commit e cria a tag `vX.Y.Z`. O push da tag dispara a publicação.

### Alternativa: instalar direto do GitHub (sem npm)

```bash
npx github:flaviogarrido/reversa#v1.3.7 install
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
- **Mudanças em `lib/`** (como as correções dos bugs de `update`/`uninstall`) podem virar PR para o upstream. Quanto menos o fork divergir em `lib/`, mais barata fica a sincronização.

---

## Checklist rápido

- [ ] Skill em `agents/fg-<nome>/` com `SKILL.md` e `agents/openai.yaml` em lockstep
- [ ] `description` sem frases-gatilho (se for *user-invoked*)
- [ ] `npm run verify` aprovado (inclui `test-fork-delivery.mjs`)
- [ ] Instalação testada num projeto descartável
- [ ] `npm pack --dry-run` lista a skill
- [x] `package.json` com nome com escopo e `update.js` usando o nome do pacote
- [ ] Trusted Publisher configurado no pacote do npm (ou, só na primeira publicação, o segredo `NPM_TOKEN`)
- [ ] `npm version patch` e `git push --follow-tags` (o workflow publica)
- [ ] Remoto `upstream` configurado e sincronizado periodicamente
