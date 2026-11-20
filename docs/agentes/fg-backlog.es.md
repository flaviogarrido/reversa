# Backlog (fg-backlog)

**Comando:** `/fg-backlog`
**Fase:** Forward (antes de `/reversa-requirements`)

---

## 📋 El gerente de producto con la planilla

El gerente de producto no escribe código ni decide la arquitectura. Anota lo que piden los clientes, da una nota de valor y de esfuerzo a cada pedido, mantiene la fila en orden y, cuando llega el momento, entrega al equipo los primeros de la fila. Su planilla nunca pierde una línea: lo que se descartó queda registrado, con el motivo.

---

## Qué hace

`fg-backlog` registra las features que describes en el chat y las mantiene en un backlog priorizado, vinculado al ciclo forward de Reversa. Solo registra y sugiere: no ejecuta el pipeline, no crea carpetas de feature y no modifica código.

Cada ítem recibe un ID (`BL-001`, `BL-002`...) que nunca se reutiliza, ni siquiera cuando el ítem se descarta. La prioridad sale de **Valor × Esfuerzo**. Puedes fijar un ítem en una posición a mano, y el resto de la fila sigue ordenado automáticamente.

Cuando un ítem se vincula a una carpeta en `_reversa_forward/`, su estado deja de ser manual y refleja la etapa real de la carpeta: `requirements`, `plan`, `coding` (con `N de M` acciones) o `done`.

Esta skill es del fork `@flaviogarrido/reversa` y no existe en el paquete `reversa` original.

---

## Operaciones

| Operación | Ejemplo de pedido | Qué pasa |
|-----------|-------------------|----------|
| Agregar | `agregar: exportar pedidos en CSV, valor alto, esfuerzo bajo` | Genera el siguiente ID con estado `proposta` y muestra la posición y el cuadrante. Si falta Valor o Esfuerzo, pregunta antes de guardar. |
| Editar | `cambiar el esfuerzo de BL-002 a alto` | Modifica el ítem y recalcula el orden automático. |
| Cambiar estado | `marcar BL-004 como lista` | Solo para ítems sin carpeta: `proposta`, `pronta` o `descartada`. Descartar exige un motivo. |
| Reordenar | `BL-007 a la posición 2` | Fija el ítem (orden `manual`). `volver a automático` lo deshace. |
| Vincular a la carpeta | `vincular BL-001 a la carpeta 012-login-sso` | Vincula el ítem a la carpeta de la feature, con tu confirmación, y deriva su estado de ella. |
| Vincular specs | `BL-003 afecta sdd/pedidos.md` | Registra qué specs o componentes del legado afecta el ítem. |
| Preparar la ejecución | `quiero llevar los 2 primeros al pipeline` | Sugiere `/reversa-requirements` (un ítem) o `/reversa-forward-autonomous` (varios) y entrega la descripción lista para pegar. No ejecuta el comando. |

---

## Cómo prioriza

| Nivel | Valor | Esfuerzo |
|-------|-------|----------|
| alto | 3 | 3 |
| medio | 2 | 2 |
| bajo | 1 | 1 |

La nota es Valor ÷ Esfuerzo. El orden automático usa la nota (descendente), luego el Valor y, por último, el ID más antiguo. El cuadrante (ganancia rápida, apuesta, relleno o evitar) es solo informativo.

Los ítems `done` bajan al final de la tabla, y los descartados salen de la tabla activa y pasan a la sección **Descartados**.

---

## Estado vinculado al ciclo forward

| Qué hay en la carpeta de la feature | Estado |
|-------------------------------------|--------|
| Sin `requirements.md` | `vazio` (con aviso) |
| `requirements.md`, sin `roadmap.md` | `requirements` |
| `roadmap.md`, sin `actions.md` | `plan` |
| `actions.md` con acciones pendientes | `coding (N de M)` |
| `actions.md` con todas las acciones hechas | `done` |

Un ítem `done` sin adenda en `_reversa_sdd/addenda/` genera la sugerencia de ejecutar `/reversa-sync`. Una carpeta que desapareció genera el aviso `pasta ausente`, y el ítem mantiene su último estado.

---

## Qué produce

| Archivo | Contenido |
|---------|-----------|
| `_reversa_sdd/backlog.md` | Tabla ordenada de los ítems activos, descripciones, sección de descartados y avisos |
| `_reversa_sdd/backlog-log.md` | Historial de cambios con fecha, autor, campo, valor anterior y nuevo, y motivo. Solo recibe líneas nuevas. |

Los dos archivos se guardan de forma atómica. Son los únicos que la skill escribe.

---

## Cuándo usar

Úsala cada vez que surja una idea de feature y quieras registrarla antes de abrir el ciclo forward, o cuando quieras decidir qué entra en el próximo `/reversa-forward-autonomous`:

```
/fg-backlog
```

Una vez creadas las carpetas de las features, ejecuta `/fg-backlog` de nuevo para vincular cada ítem a su carpeta.
