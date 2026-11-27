# Etapa 2: implementação (2026-11-27)

Pedido do usuário: "pode fazer o T2 inteiro sem parar, vamos concluir tudo que tem para fazer, aí a gente só fica
treinando no final". Branch `feat/sim-training-env` (PR #1). Este documento diz **o que existe, como foi testado e
o que falta medir com treino**. Os resultados de evolução (T2.9) só valem depois do treino longo e da avaliação.

## Estado das tasks (004)
| Task | Estado | Evidência |
|---|---|---|
| T2.0a Core com sentidos, plantas e corpo | ✅ no Sim; o Godot usa o mesmo layout e helpers do Core | 111 testes C# |
| T2.0b Sim N mundos | ✅ (noite anterior) | |
| T2.0c snapshot atômico | ✅ agora com genomas, gestação, carcaças e arquivo | teste de hash com gravidez |
| T2.0d Godot como visualizador | ✅ `watch.ps1 -Sim <run>`: desenha o snapshot e recarrega quando o Sim salva | `benchmarks/screenshots/t2.0d-viewer-*.png` |
| T2.0e cérebro mais barato | ✅ GRU fundida, slots vazios fora do update (48 ms/tick com 288 criaturas); fp16 opcional (`-Amp`), sem avaliação | |
| T2.1a ilha | ✅ e ligada ao Sim (`-Island`), cheiro de mar contínuo | bench 55 ticks/s com 200 criaturas |
| T2.1b nadar cansa, afogamento | ✅ causa de morte `Drowning` (10) | |
| T2.1c ano de 12 dias, estações por região, seca, fotoperíodo | ✅ inverno mais forte ao norte, lagos rasos secam no fim do verão | teste da seca |
| T2.1d escala | ✅ 200 criaturas: 61,8 ticks/s = 6,2× tempo real (meta ≥ 2×) | bench |
| T2.2a genética | ✅ ligada: cada criatura tem genoma diploide | |
| T2.2b corpo modular | ✅ corpo pelos genes (velocidade, degrau, escalar só com garras, cavar, nadar, armadura, Kleiber); visual = caixa no tamanho do corpo (partes ainda sem modelo próprio) | |
| T2.2c ciclo de vida | ✅ Gompertz, crescimento, maturidade, ninhada, gestação, estação reprodutiva, torpor (gene) | testes de velhice e torpor |
| T2.2d sentidos por gene | ✅ alcance/ângulo de visão, visão noturna, olfato, audição | |
| T2.2e motivações como genes | ✅ 9 genes `m_*` (inclui cuidado); a recompensa lê do protocolo | testes de recompensa |
| T2.3 protocolo v3 | ✅ 1130 observações, 13 ações, eventos BIRTH/MATED/FED | fixture + round-trip C#/Python |
| T2.4a acasalar, gestação, nascimento, alimentar, parentesco | ✅ | teste "filho dos dois pais" |
| T2.4b população por nascimentos, resgate, teto, espécies D1 | ✅ o Sim é dono da população; resgate pelo arquivo de pais recentes | testes de resgate |
| T2.5 predadores | ✅ dieta por gene, mordida, armadura, carcaça que apodrece, carniça; ⏳ "coexistem 5 anos" só com treino | teste da mordida e da carcaça |
| T2.6a instinto herdado | ✅ ~3,3k pesos, crossover + mutação no BIRTH, α cai com a idade | 4 testes |
| T2.6b memória longa | ✅ `-Rollout 256/512`; 512 cabe em 6 GB (pico 3,8 GB) | medição |
| T2.7a genealogia | ✅ JSONL escrito pelo Sim → SQLite (`creature_brain.genealogy`) | teste |
| T2.7b observatório | ✅ no visualizador: Tab (espécies, genes, genealogia), inspetor Genes/Família | screenshots |
| T2.8 controle sem seleção | ✅ `-Control`: pais sorteados entre adultos, mesma genética | teste |
| T2.9 avaliação | ✅ ferramenta `stage2.ps1` (seleção × controle) e `stage2_eval`; ⏳ **resultado depende do treino** | teste com dados sintéticos |

## Decisões tomadas nesta etapa (revisáveis)
| Decisão | Motivo |
|---|---|
| O Sim é dono da população; ids ≥ 2³⁰ são do Sim, o cérebro não faz mais respawn | nascimento e morte acontecem no mundo; sem colisão de ids |
| Fundadores nascem jovens adultos | a primeira geração precisa poder se reproduzir |
| Libido sobe em adultos alimentados e zera ao acasalar; setpoint da libido = 0 | sem isso acasalar não dá recompensa (a libido nunca mudava) |
| Motivação "cuidado" (9ª) recompensa energia dada ao próprio filhote (evento FED) | D3: dá motivo evolutivo para alimentar filhote |
| Distância de espécie 0,06 | com 0,15 os fundadores A × B tinham ~70 % de híbridos viáveis |
| Escalar só com garras funcionais | regra do BodyPlan: parte abaixo do limiar não funciona |
| Avaliação da etapa 1 roda sem nascimentos (`--no-births`) | compara vida com vida, como antes |
| Instinto só no treino com nascimentos (padrão α₀ = 0,5) | é gene; sem nascimento não há herança |

## Achados honestos
- **Cheiro em escala log sozinho não resolveu a sede** num treino curto (`p3-mlp`, 24 000 ticks): vida 544 → 603 → 576 s
  contra 530 → 658 → 742 do v2. Com sede, perto d'água só 0,17 % do tempo. O gargalo da etapa 1 continua. A etapa 2
  muda o problema (fundadores adultos, seleção), mas achar água ainda é a competência básica a acompanhar.
- Dois bugs pegos pelos testes: o tempo do último recálculo do corpo não ia no snapshot (continuação divergia); e a
  chance de morte de Gompertz dava 0/0 em idade alta (a criatura nunca morria de velhice).
- Pré-alocar slots para nascimentos deixou o cérebro 5× mais lento (400 ms/tick) até tirar do update os slots vazios.

## Resultado do treino longo `s2-a` (T2.9): FAIL
288 000 ticks, 4 mundos × 48, seleção (`s2-a-sel`) × controle (`s2-a-ctl`). Relatório: `benchmarks/runs/eval-s2-a/summary.md`.

| | Nascidos | Concepções | Resgates | Geração máx. | Mortes (fome / sede / frio) |
|---|---|---|---|---|---|
| seleção | 0 | 2 (1 inviável) | 3802 | 0 | 2765 / 958 / 181 |
| controle | 3294 | — | 1776 | 2 | 4192 / 781 / 195 |

Os 5 critérios (reprodução sustentada, mudança direcional × controle, Baldwin, r > K, estação) deram FAIL.

**Diagnóstico:**
1. **A política não aprendeu.**
   - O `actor_log_std` terminou exatamente no teto (−0,5) nas 13 ações, nos dois runs. A entropia ficou presa em 11,946 (o máximo) desde ~1500 updates.
   - `reward_mean` ficou em torno de −0,005 o run inteiro.
   - Com recompensa tão pequena e vantagens normalizadas, o bônus de entropia vence: a política fica quase aleatória.
   - É o mesmo sintoma do teto de entropia da etapa 1, que baixar o teto só mascarou.
2. **Acasalar exige coordenação rara.**
   - Os dois precisam ter a ação 8 > 0,5 ao mesmo tempo, a menos de 2 m, com energia ≥ 0,55, e a fêmea precisa estar na estação.
   - A fome mata 70 % das criaturas, então energia ≥ 0,55 é rara.
3. **O resgate sem arquivo devolve fundadores.**
   - O arquivo só recebe genomas quando há acasalamento. Sem acasalar, todo resgate é fundador novo: a população fica no piso (64) e não há gerações.
4. **O controle prova que o resto da cadeia funciona** (nascimento, herança, gerações e genealogia): só falta a decisão de acasalar.

**Próximo passo proposto:**
- (a) Corrigir o aprendizado: log_std fixo e decrescente (−0,5 → −1,5), sem bônus de entropia, e escala da recompensa ×10.
- (b) Repetir um treino curto da etapa 1 para ver se a sobrevivência sobe.
- (c) Só então refazer `s2-a`.

## Como treinar e avaliar
```powershell
.\scripts\stage2.ps1 -Name s2-a -Ticks 288000 -Worlds 4            # seleção + controle lado a lado; retomável
.\scripts\stage2.ps1 -Name s2-a -EvalOnly                          # relatório benchmarks/runs/eval-s2-a
.\scripts\watch.ps1 -Sim s2-a-sel                                  # assistir (Tab = observatório)
python -m creature_brain.genealogy report benchmarks/runs/s2-a-sel # gerações, causas, genes por quarto
```
