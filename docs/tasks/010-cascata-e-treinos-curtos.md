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
### Degrau A: `f1-mlp` (só a correção 1, layout v3, 24 000 ticks, 6 mundos, ~25 min)
- **Entropia:** 5,45 → 4,9 e caindo. No `p3-mlp`, com a mesma configuração mas sem a correção, ficou parada em 11,9 (o máximo).
- **Seed 0, 6 réplicas** (`benchmarks/runs/eval-f1`): vida **408 s [393–425]** contra 313 s do aleatório = **1,31× [1,23–1,38]**.
  - Come com fome: 2× o aleatório. Dorme cansada: 21× o aleatório.
  - Mortes: fome 58 %, sede 41 %.
- **Conclusão:** o aprendizado voltou, mas ainda é fraco. A sede continua sendo o gargalo.
- **Nota:** o `p3-mlp` tem layout 1124 (anterior ao torpor) e não carrega mais. Por isso a comparação é só contra o aleatório. A avaliação rodou numa worktree no commit f98d095 (v3).

### Degrau B: treinos curtos com nascimentos (protocolo v4, MLP, 4 mundos × 48, 24 000 ticks, ~10 min cada)
| Run | Mudança | Concepções | Nascimentos | Encontros¹ | Macho escolhe acasalar | Mortes: fome / sede |
|---|---|---|---|---|---|---|
| `b1` | cascata v4 (feromônio, fêmea receptiva, ordem das necessidades) | 1 | 1 | — | — | — |
| sonda aleatória | contadores de diagnóstico | 2 | 0 | **9** | 23 % | 81 % / 17 % |
| sonda aleatória + bandos | fundadores nascem em bandos por espécie | 46 | 42 | 203 | 24 % | — |
| `b2` | bandos | 33 | 38 | 690 | **12 %** (aprendeu a evitar) | — |
| `b3` | macho paga 0,02 (antes 0,2); `m_libido` 0,3 → 1,0 | 43 | 39 | 679 | 29 % | 60 % / 37 % |
| `b4` | bando mora na margem de um lago que não seca | 44 | **67** | 669 | 10 % | **78 % / 21 %** |

¹ Fêmea × tick no cio com macho fértil a ≤ 2 m.

**O que se aprendeu:**
- O encontro era o gargalo do acasalamento. Com bandos, os encontros subiram de 9 para cerca de 680 por run.
- A sede caiu com o bando perto do lago: de 37 % para 21 % das mortes.
- Em todos os runs, os nascimentos acontecem só nos primeiros ~4000 ticks. Depois a população cai para o piso (64), por fome, e quase ninguém chega a energia ≥ 0,55: não há cio nem macho fértil.
- Os filhotes morrem com ~480 s de idade, em média.
- No `b4`, o macho voltou a evitar acasalar. Com fome, o alívio da libido quase não vale (pela ordem das necessidades), e o custo continua existindo.

**Gargalo atual: comer.** É o primeiro degrau da cascata ("nasce, quer comer, come"). O próximo treino curto ataca a alimentação: medir "come quando tem fome e há planta perto" e ver se a planta perto do bando acaba.

### Degrau "comer" (sondas de comida no relatório do Sim)
- **Sonda aleatória** (6000 ticks): fome 58 % do tempo; perto de planta 3,6 % (com fome, 1,7 %); come quando está perto 24 %. Biomassa perto do bando: 26 → 17,5.
- **Bug de física.** O gasto de energia segue Kleiber (M^0,75), mas a reserva era fixa em 1. O bicho grande (B, massa ~4) gastava metade da energia em 74 s parado, mais rápido que o pequeno.
  - **Correção:** toda variação de energia (comida e gasto) é dividida pela massa atual. Parado até metade da energia: A 131 s, C 194 s, B 255 s.
  - **Teste:** `Should_StarveSlower_When_BodyIsBigger`.
- **`b5`** (com a correção): 46 concepções, 32 nascimentos.
  - Fome com planta perto: 4,3 % (era 1,7 %). Mortes: fome 80 %, sede 19 %.
  - Os filhotes morrem com 143 s: são minúsculos, e a reserva acompanha a massa.
  - "Come quando há planta perto" ficou em 25,5 %, **igual ao aleatório: o cérebro ainda não aprendeu a comer**.
- **Próximo:** `a1-mlp` (degrau A, sem nascimentos), para ver se comer é aprendido. Depois: filhote nasce com reserva mínima ou é alimentado.

### `a1-mlp` (degrau A, sem nascimentos, 24 000 ticks, 6 mundos) × sonda aleatória (`diag-food-a`, 6000 ticks)
| | aleatório | `a1` (treinado) |
|---|---|---|
| perto de planta | 8,4 % | **49 %** |
| com fome e perto de planta | 4,2 % | **51 %** |
| come quando perto de planta | 24,7 % | 23,4 % |
| come **com fome** perto de planta | 24,3 % | (contador novo; medir no próximo run) |
| bebe quando perto d'água | 24,7 % | **9,7 %** |
| tempo com fome / com sede | 44 % / 29 % | 38 % / 44 % |

**Leitura:**
- O cérebro **aprendeu a ficar perto de comida** (49 % contra 8 %), mas **não aprendeu a apertar "comer"**.
- Desaprendeu beber: bebe menos que o aleatório.
- **Hipótese a testar:** comer ou beber já saciado, acima do setpoint 0,8, dá recompensa negativa (`|x − setpoint|` sobe). Como na maior parte do tempo a criatura não está com fome, "comer pouco" ganha.

**Próximo microteste:**
1. Rodar de novo com os contadores condicionais ("come com fome", "bebe com sede"). Se continuar ~24 %, a política não separa com fome de sem fome.
2. Correção candidata: não punir passar do setpoint ao comer ou beber (saciedade = 0 de recompensa, não negativa), como foi feito com a libido.
