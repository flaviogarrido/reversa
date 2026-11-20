# Customizando o Reversa no fork

Guia para incluir skills próprias neste fork (`flaviogarrido/reversa`), testar, entregar aos projetos e continuar acompanhando o upstream (`sandeco/reversa`).

> **Resumo:** criar uma skill é só uma pasta em `agents/`. O cuidado está na **entrega**: do jeito que está, o fork instala e atualiza a partir do pacote `reversa` original do npm, não a partir deste repositório.

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
3. **Evita um bug conhecido do `update`.** Em `lib/commands/update.js`, a checagem "arquivo modificado pelo usuário" usa `startsWith(relDir)` sem a `/` final. Por isso, editar qualquer arquivo de uma skill `reversa-xyz` instalada bloqueia a atualização da skill `reversa` (o orquestrador). A correção é comparar com `relDir + '/'`.

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

## 3. Entrega: o problema do fork

Do jeito que está hoje:

- `npx reversa install` baixa o pacote **original** do npm, sem as suas skills.
- `lib/commands/update.js` compara a versão instalada com o `latest` do pacote **`reversa`** no npm (`fetchLatestVersion('reversa')`). Se a versão do seu fork for igual ou maior que a do upstream, o update responde *"already on the latest version"* e **não atualiza nada**.

### Recomendado: publicar com escopo no npm

1. Em `package.json`, mude `"name"` para `"@flaviogarrido/reversa"`. O comando (`"bin": { "reversa": ... }`) pode continuar o mesmo.
2. Em `lib/commands/update.js`, troque o literal `'reversa'` em `fetchLatestVersion('reversa')` pelo nome lido do `package.json` (`pkg.name`).
3. Publique:

   ```bash
   npm publish --access public
   ```

4. Os projetos passam a usar:

   ```bash
   npx @flaviogarrido/reversa install
   ```

   ```bash
   npx @flaviogarrido/reversa update
   ```

### Alternativa: instalar direto do GitHub (sem npm)

```bash
npx github:flaviogarrido/reversa#v1.3.7 install
```

Fixe uma tag (`#vX.Y.Z`) para ter builds reproduzíveis. Mesmo assim, a verificação de versão do `update` continua consultando o pacote errado no npm, então o passo 2 acima também vale aqui.

### O que o update garante

- Ele preserva arquivos editados pelo usuário: um manifesto SHA-256 fica em `.reversa/_config/files-manifest.json`.
- Ele instala automaticamente skills novas do pacote, porque `update` reinstala tudo o que existe em `agents/`.
- Ele acrescenta a política de edição do legado aos arquivos de entrada (`CLAUDE.md`, `AGENTS.md` etc.) sem apagar o que o usuário escreveu.

### Rotina de release

1. Suba a versão: `npm version patch` (ou `minor`).
2. Rode as verificações da seção 2.
3. Faça push com a tag: `git push --follow-tags`.
4. `npm publish` (se usar o npm).

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
- [ ] `verify-invocation.py` e `test-installer-transport.mjs` aprovados
- [ ] Instalação testada num projeto descartável
- [ ] `npm pack --dry-run` lista a skill
- [ ] `package.json` com nome com escopo e `update.js` usando `pkg.name`
- [ ] Versão incrementada, tag criada, pacote publicado
- [ ] Remoto `upstream` configurado e sincronizado periodicamente
