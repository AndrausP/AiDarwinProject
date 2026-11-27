# Proposta — Protocolo v3 (T2.3) · para aprovação do usuário

Escrita na noite de 2026-11-27 pelo agente. **Aprovada pelo usuário na manhã de 2026-11-27 (P1–P5 como recomendado); implementada** em `contracts/protocol-v3.json` (bloco de tempo: luz atual e oferta térmica no lugar de sen/cos do ano, que repetiam o fotoperíodo). Texto original: mudar o layout de observação/ações
é nova versão de protocolo e retreino do zero (regra do projeto). Este documento junta o que a etapa 2 precisa e o que
a noite mostrou sobre o gargalo da etapa 1.

## Por que agora
1. As bibliotecas da etapa 2 já existem no Core (genética, corpo, sentidos, ciclo de vida, acasalamento, ilha), mas o
   cérebro não as vê: falta o protocolo levar genes, sinais sociais e motivações.
2. A sonda de água mostrou que o v2 **sabe beber** (85 % das vezes que está na margem) mas **não acha a água quando tem
   sede**. O cheiro de água hoje é `Σ 1/(1+d²)`: a 30 m vale ~0,001 e a diferença nariz−corpo some no ruído. É um sinal
   que o cérebro quase não consegue usar a mais de 20 m.

## Layout proposto (v3)
| Bloco | v2 | v3 | Mudança |
|---|---|---|---|
| Visão | 72 × 13 = 936 | 72 × 14 = 1008 | classe nova **água salgada** (o mar parece água mas não mata a sede: precisa ser distinguível pela visão ou o animal só aprende provando) |
| Setores de criaturas | 4 × 5 = 20 | 4 × 9 = 36 | + parente (cheiro de família), sexo, maduro, filhote |
| Audição | 8 | 8 | — |
| Olfato | 12 | 15 | intensidade em **escala log** (`log1p(k·s)/log1p(k)`) para o gradiente sobreviver à distância; + **água doce** separada de água salgada (3 valores) |
| Propriocepção | 10 | 10 | — |
| Interocepção | 12 | 14 | + gestação/gema, + "filhote por perto precisando" |
| Tempo | 2 | 6 | + fotoperíodo (duração do dia) e tendência (dias crescendo/encurtando), + estação (sen, cos) |
| Genes expressos | — | 19 | vetor fixo do catálogo (`GeneCatalog.Basic`), normalizado 0..1 pela faixa de cada gene |
| Motivações | — | 8 | pesos da recompensa do indivíduo (D3) — o cérebro sabe "do que gosta" |
| **Total** | **1000** | **1124** | |

**Ações** (12 → 13): + `feed` (alimentar filhote próximo); `mate` passa a ter efeito (regras de `Mating`).

**Eventos novos:** `BIRTH` (filho, mãe, pai), `MATED`. **SPAWN** do Python deixa de criar vidas no treino padrão:
nascimentos vêm do Sim (T2.4b); o Python só pede "resgate" quando a população cai abaixo do mínimo (arquivo anti-extinção).

## Decisões pendentes (preciso de você)
| # | Pergunta | Recomendação do agente |
|---|---|---|
| P1 | Olfato em escala log (muda o significado do sinal — v2 não serve mais) | **Sim**: é a correção mais barata para o gargalo da sede, e o v3 já retreina do zero |
| P2 | Classe de visão "água salgada" | **Sim** (a alternativa é aprender só provando, caro e lento) |
| P3 | Genes como entrada do cérebro base (19 floats hoje; cresce com o catálogo) | **Sim** (plano: o cérebro vê o próprio corpo e se adapta) |
| P4 | Retreinar v3 do zero no v3 com ilha já na primeira rodada, ou primeiro no mapa da etapa 1 | **Mapa da etapa 1 primeiro** (isola o efeito do protocolo), ilha na rodada seguinte |
| P5 | Fechar a etapa 1 com o v2 (melhor na seed 0 hoje) mesmo sem bater os critérios, ou insistir mais | **Fechar com o diagnóstico documentado**: os critérios "beber com sede ≥ 2×" e "vida ≥ 3×" dependem de achar água, que o v2 não consegue com o sinal atual; insistir no v2 é gastar GPU no limite conhecido do protocolo |

## Plano depois de aprovado
1. `contracts/protocol-v3.json` + fixture + codecs C# e Python (testes de ida e volta nos dois lados).
2. Sim no v3: genoma por criatura (fundadores de D1), corpo e sentidos pelos genes, olfato log, água salgada visível.
3. Retreino do zero do v3 (sem curiosidade) no mapa da etapa 1 e avaliação na seed 0 contra o aleatório.
4. Nascimentos (T2.4a/b) → população por reprodução → controle sem seleção (T2.8) → avaliação da etapa 2.
