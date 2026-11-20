# Template do backlog

Modelo usado pelo `fg-backlog` para criar e validar os arquivos de saída.

## `_reversa_sdd/backlog.md`

```markdown
# Backlog do produto

Atualizado em: <ISO 8601>
Método de prioridade: Valor × Esforço (ver fg-backlog/references/priorizacao.md)

## Itens ativos

| Pos | ID | Título | Valor | Esforço | Ordem | Status | Pasta | Afeta | Adendo |
|---|---|---|---|---|---|---|---|---|---|
| 1 | BL-003 | Exportar pedidos em CSV | alto | baixo | auto | pronta | | sdd/pedidos.md | |
| 2 | BL-001 | Login com SSO | alto | alto | manual | coding (4 de 9) | 012-login-sso | AuthService | |
| 3 | BL-002 | Filtro por data | médio | médio | auto | done | 010-filtro-data | sdd/relatorios.md | addenda/010-filtro-data.md |

## Descrições

### BL-001 · Login com SSO
<problema e resultado esperado, como o usuário descreveu>

### BL-002 · Filtro por data
...

## Descartados

| ID | Título | Descartado em | Motivo |
|---|---|---|---|

## Avisos

- BL-00X: pasta `NNN-nome` ausente em `_reversa_forward/`.
- BL-00Y: done sem adendo. Rodar `/reversa-sync`.
```

Regras do formato:

- **Pos**: posição atual, de 1 a N, sem buracos. Itens descartados não têm posição.
- **Ordem**: `auto` (calculada) ou `manual` (fixada pelo usuário).
- **Status**: `proposta`, `pronta` ou `descartada` (manuais, sem pasta) ou `requirements`, `plan`, `coding (N de M)`, `done`, `vazio` (derivados, com pasta).
- **Afeta**: lista separada por vírgula, com caminhos relativos a `_reversa_sdd/` ou nomes de componentes.
- Células vazias ficam em branco, nunca com `-` ou `N/A`.

## `_reversa_sdd/backlog-log.md`

```markdown
# Histórico do backlog

| Quando | Autor | ID | Campo | Antes | Depois | Motivo |
|---|---|---|---|---|---|---|
| 2025-01-10T14:32:00-03:00 | Flavio | BL-003 | criado | | proposta | |
| 2025-01-10T14:35:00-03:00 | Flavio | BL-001 | posição | 3 | 2 (manual) | prioridade comercial |
| 2025-01-12T09:00:00-03:00 | fg-backlog (sync) | BL-001 | status | plan | coding (0 de 9) | |
```

Campos possíveis: `criado`, `título`, `descrição`, `valor`, `esforço`, `posição`, `ordem`, `status`, `pasta`, `afeta`, `adendo`.
