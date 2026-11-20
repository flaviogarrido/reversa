# Priorização Valor × Esforço

## Pontuação

| Nível | Valor | Esforço |
|---|---|---|
| alto | 3 | 3 |
| médio | 2 | 2 |
| baixo | 1 | 1 |

Nota = Valor ÷ Esforço.

| Quadrante | Condição | Leitura |
|---|---|---|
| Ganho rápido | Valor alto, Esforço baixo | fazer primeiro |
| Aposta | Valor alto, Esforço alto | planejar |
| Preenchimento | Valor baixo/médio, Esforço baixo | quando houver folga |
| Evitar | Valor baixo, Esforço alto | candidato a descarte |

Mostre o quadrante ao usuário quando um item entrar ou mudar de Valor ou Esforço. É só informativo e não muda a regra de ordem.

## Ordem automática

Entre os itens com `Ordem = auto`, ordene por:

1. Nota, decrescente.
2. Valor, decrescente (desempate).
3. ID, crescente (o mais antigo primeiro).

## Ordem manual

1. Itens com `Ordem = manual` ficam fixos na posição que o usuário escolheu.
2. As posições restantes são preenchidas pelos itens `auto`, na ordem acima.
3. Se dois itens manuais disputarem a mesma posição, o mais recente fica nela e o anterior desce uma posição. Avise o usuário.
4. Se a posição manual for maior que o total de itens, o item vai para a última posição.

## Itens fora da ordenação

- Itens `descartada` saem da tabela ativa e não têm posição.
- Itens `done` continuam na tabela, mas descem para o fim, na ordem do ID. Não contam para a fila de execução.
- Itens já ligados a uma pasta mantêm a posição, mas não entram na fila sugerida para `/reversa-forward-autonomous`.
