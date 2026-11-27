# 010: aprendizado corrigido, cascata de necessidades e treinos curtos (2026-11-27)

Pedidos do usuário depois do `s2-a` (T2.9 FAIL):
- "por que não fazemos algo em cascata: ele nasce, quer comer, come, acha um parceiro, fica perto, segue a fêmea, ela emite feromônio e ele ativa também"
- "faça treinos mais curtos e vá corrigindo os erros de forma gradual; nada de treinos longos, a não ser que os treinos curtos deem resultado hábil"

## Regra de trabalho
- Treinos curtos (≤ 45 min), uma correção por vez, sempre medidos.
- Treino longo só quando um curto mostrar ganho claro.

## Correções
| # | Correção | Por quê | Teste |
|---|---|---|---|
| 1 | `actor_log_std` começa em −1,0 (antes −0,5 = teto) e é projetado para dentro dos limites depois de cada passo do otimizador | `clamp()` tem gradiente zero fora do intervalo: o parâmetro passou um fio do teto (−0,498) e congelou. No `s2-a`, as 13 ações ficaram com desvio máximo o run inteiro e a política ficou quase aleatória | `test_log_std_bounds.py` |
| 2 | Protocolo v4 (1133 observações): +3 de olfato = feromônio de acasalamento | Machos maduros sentem fêmeas no cio; fêmeas no cio sentem machos férteis. Alcance maior que o cheiro de corpo (peso 4). Seguir o cheiro continua sendo aprendido | `Should_SmellMatingPheromone_…` |
| 3 | Fêmea no cio é receptiva sem agir | No cio = madura, alimentada (energia ≥ 0,55), sem gravidez, na estação. Basta o macho escolher acasalar a ≤ 2 m. Antes os dois precisavam escolher ao mesmo tempo, o que é raríssimo com política aleatória | `Should_Conceive_When_OnlyTheMaleChooses…` |
| 4 | Ordem das necessidades na recompensa | Só o alívio da libido (acasalar) é recompensado, nunca a subida dela: antes, estar alimentado subia a libido e punia. E o alívio vale só na medida em que fome e sede estão resolvidas: `clip((min(energia, água) − 0,4)/0,3, 0, 1)`. Comer e beber vêm primeiro | `test_reward.py` (2 novos) |

Nenhuma dessas é script de comportamento. O cérebro continua decidindo tudo, e o mundo só ganhou sinais reais (cheiro, cio).

## Degraus de treino (currículo)
| Degrau | O que roda | Passa quando |
|---|---|---|
| A | Sobreviver (`-NoBirths`) | O curto mostra que o aprendizado anda (entropia cai, vida sobe contra o aleatório na seed 0) |
| B | Nascimentos com seleção, curto | Concepções escolhidas pelo cérebro > 0 e crescendo |
| C | Seleção × controle (`stage2.ps1`) | Só depois de B dar resultado |

## Resultados
(preenchido conforme os treinos curtos terminam)
