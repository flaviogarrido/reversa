# Backlog (fg-backlog)

**Comando:** `/fg-backlog`
**Fase:** Forward (antes do `/reversa-requirements`)

---

## 📋 O gerente de produto com a planilha

O gerente de produto não escreve código nem decide a arquitetura. Ele anota o que os clientes pedem, dá uma nota de valor e de esforço a cada pedido, mantém a fila em ordem e, quando chega a hora, entrega ao time os primeiros da fila. A planilha dele nunca perde uma linha: o que foi descartado fica registrado, com o motivo.

---

## O que faz

O `fg-backlog` registra as features que você descreve no chat e as mantém num backlog priorizado, ligado ao ciclo forward do Reversa. Ele só registra e sugere: não executa o pipeline, não cria pastas de feature e não altera código.

Cada item recebe um ID (`BL-001`, `BL-002`...), que nunca é reutilizado, nem quando o item é descartado. A prioridade sai de **Valor × Esforço**. Você pode fixar um item numa posição à mão, e o resto da fila continua ordenado automaticamente.

Quando um item ganha uma pasta em `_reversa_forward/`, o status deixa de ser manual e passa a refletir o estágio real da pasta: `requirements`, `plan`, `coding` (com `N de M` ações) ou `done`.

Esta skill é do fork `@flaviogarrido/reversa` e não existe no pacote `reversa` original.

---

## Operações

| Operação | Exemplo de pedido | O que acontece |
|----------|-------------------|----------------|
| Adicionar | `adicionar: exportar pedidos em CSV, valor alto, esforço baixo` | Gera o próximo ID, status `proposta`, e mostra a posição e o quadrante. Se faltar Valor ou Esforço, pergunta antes de gravar. |
| Editar | `mudar o esforço do BL-002 para alto` | Altera o item e recalcula a ordem automática. |
| Mudar status | `marcar o BL-004 como pronta` | Só para itens sem pasta: `proposta`, `pronta` ou `descartada`. Descartar exige motivo. |
| Reordenar | `BL-007 para a posição 2` | Fixa o item (ordem `manual`). `voltar para automático` desfaz. |
| Ligar à pasta | `ligar o BL-001 à pasta 012-login-sso` | Liga o item à pasta da feature, com sua confirmação, e passa a derivar o status dela. |
| Ligar specs | `o BL-003 afeta sdd/pedidos.md` | Registra quais specs ou componentes do legado o item afeta. |
| Preparar a execução | `quero levar os 2 primeiros para o pipeline` | Sugere `/reversa-requirements` (um item) ou `/reversa-forward-autonomous` (vários) e entrega a descrição pronta para colar. Não roda o comando. |

---

## Como prioriza

| Nível | Valor | Esforço |
|-------|-------|---------|
| alto | 3 | 3 |
| médio | 2 | 2 |
| baixo | 1 | 1 |

A nota é Valor ÷ Esforço. A ordem automática usa a nota (decrescente), depois o Valor e, por fim, o ID mais antigo. O quadrante (ganho rápido, aposta, preenchimento ou evitar) é só informativo.

Itens `done` descem para o fim da tabela, e itens descartados saem da tabela ativa e vão para a seção **Descartados**.

---

## Status ligado ao ciclo forward

| O que há na pasta da feature | Status |
|------------------------------|--------|
| Sem `requirements.md` | `vazio` (com aviso) |
| `requirements.md`, sem `roadmap.md` | `requirements` |
| `roadmap.md`, sem `actions.md` | `plan` |
| `actions.md` com ações pendentes | `coding (N de M)` |
| `actions.md` com todas as ações feitas | `done` |

Um item `done` sem adendo em `_reversa_sdd/addenda/` gera a dica de rodar `/reversa-sync`. Uma pasta que sumiu gera o aviso `pasta ausente`, e o item mantém o último status.

---

## O que ele produz

| Arquivo | Conteúdo |
|---------|----------|
| `_reversa_sdd/backlog.md` | Tabela ordenada dos itens ativos, descrições, seção de descartados e avisos |
| `_reversa_sdd/backlog-log.md` | Histórico de mudanças com data, autor, campo, valor anterior e novo, e motivo. Só recebe novas linhas. |

Os dois arquivos são gravados de forma atômica. São os únicos que a skill escreve.

---

## Quando usar

Use sempre que surgir uma ideia de feature e você quiser registrá-la antes de abrir o ciclo forward, ou quando quiser decidir o que entra no próximo `/reversa-forward-autonomous`:

```
/fg-backlog
```

Depois que as pastas das features forem criadas, rode `/fg-backlog` de novo para ligar cada item à sua pasta.
