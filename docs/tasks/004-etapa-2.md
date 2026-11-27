# Task 004 — Etapa 2: simulador próprio, ilha, genes, reprodução, predadores

**Depende de:** T1.8 (etapa 1 fechada) · **Decisões de origem:** `002-voxel-evolucao.md` · **Fecha com:** T2.9.

## Decisões (D1–D4 adotadas em 2026-11-27 pelo agente com autonomia delegada; registro em `007-noite-2026-11-27.md`)
| # | Pergunta | Recomendação (**adotada**) | Bloqueia |
|---|---|---|---|
| D1 | **Espécies iniciais:** o plano antigo diz "herbívoro + carnívoro"; o novo diz "A vive 3 anos, B vive 20". Qual a dieta de cada uma? | **3 espécies:** A herbívoro pequeno (r, 3 anos), B herbívoro grande (K, 20 anos), C carnívoro médio (~8 anos, minoria) | T2.5, T2.6 |
| D2 | **Quem é dono da genética:** Python (como no plano antigo) ou C# Core? | **C# Core**: o simulador roda tudo localmente; o Python só recebe os genes expressos | T2.3 |
| D3 | **Motivação como gene:** com recompensa só homeostática, o PPO não tem motivo para acasalar nem cuidar de filhote | **pesos da recompensa por indivíduo são genes** (fome, sede, medo, libido, cuidado com parentes); o cérebro vê esses pesos na entrada. A evolução escolhe as motivações e o PPO aprende a satisfazê-las | T2.3, T2.4, T2.6 |
| D4 | **Godot depois do simulador:** re-simular no Godot ou só desenhar o estado do Sim? | **só desenhar:** o Godot carrega a mesma DLL do Core e mostra snapshots ou o estado ao vivo do Sim; um código de simulação só | T2.1 |

## Tasks
| ID | Task | Depende de | Entrega | Aceite |
|---|---|---|---|---|
| **T2.0** | **Simulador próprio** | | | |
| T2.0a | Mover para o Core os sentidos (visão, olfato, som), as plantas, o corpo da criatura e o campo sensorial | T1.8 | `CreatureIA.Core/Creatures`, `/Senses`; Godot passa a usar o Core | teste de paridade: mesma seed → mesma observação (tolerância float) · **⏳ 2026-11-27: lógica portada em `CreatureIA.Core/Simulation` (paridade estatística validada); falta o Godot passar a usar o Core** |
| T2.0b | `CreatureIA.Sim`: console .NET, N mundos em threads, lote de observações, ponte (TCP em lote primeiro; depois memória compartilhada) | T2.0a | executável + `scripts/train.ps1` | ≥ 5× passos/s contra o Godot headless na mesma máquina, determinístico por mundo · **✅ 2026-11-27 (TCP em lote; v3 ~3,8× por run e 2 runs simultâneos; memória compartilhada fica para depois)** |
| T2.0c | **Persistência do mundo:** snapshot atômico (blocos editados, criaturas, genomas, plantas, relógio, RNG) e retomada | T2.0b | `WorldSnapshot` + testes | parar e retomar dá a mesma continuação (hash de estado) · **✅ 2026-11-27** (`SimWorld.Save/Load`, teste de hash; `train.ps1` retoma os mundos pelo RunName) |
| T2.0d | Godot como visualizador do Sim (D4): abre um snapshot ou acompanha ao vivo | T2.0c | `watch.ps1 -Sim` | assistir um run do Sim sem re-simular |
| T2.0e | Cérebro mais barato: fp16 + GRU fundido no treino; benchmark notebook (3050) × desktop (2060) | T1.8 | `--amp`, relatório | ≥ 1,5× atualizações/s sem perda na avaliação · **⏳ 2026-11-27: GRU fundido (update 4,2×) e overhead Python cortado (cérebro 132 → 80 ms/tick); falta fp16** |
| **T2.1** | **Ilha e clima** | | | |
| T2.1a | Gerador da ilha: oceano salgado (bloco `SaltWater`), praias, cordilheira nevada, rios das nascentes ao mar, lagos, biomas por clima regional; sem parede | T2.0a | `IslandGenerator` + testes (rio conecta montanha ao mar, mar não mata a sede) | screenshot + testes · **✅ 2026-11-27** (5 testes; `benchmarks/screenshots/t2.1a-ilha.png`; `CREATURE_IA_ISLAND=1` só visualização, treino segue no mapa da etapa 1) |
| T2.1b | Nadar cansa; mar aberto mata por exaustão ou frio (nova causa de morte: afogamento) | T2.1a | fisiologia + protocolo (causa de morte) | teste: nadar indefinidamente leva à morte |
| T2.1c | Tempo: ano de 12 dias, estações por região, lagos rasos secam e voltam, plantas por região e estação, fotoperíodo | T2.1a | `WorldCycles` por região | testes analíticos de clima e seca |
| T2.1d | Escala: chunks uniformes comprimidos, malha por distância no visualizador, população 150–250 | T2.1a | medições | budget: ≥ 2× tempo real por mundo com 200 criaturas no Sim |
| **T2.2** | **Genoma e corpo** | | | |
| T2.2a | Genética diploide: alelos valor + dominância, XY, crossover, mutação (taxa é gene, tende a recessiva), distância genética, viabilidade do filho | D2 | `CreatureIA.Core/Genetics` + testes (proporções mendelianas 3:1) | testes estatísticos passam · **✅ biblioteca 2026-11-27** (9 testes: 3:1 qui², sexo 1:1, ligação, mutação recessiva, taxa herdável; ainda não ligada ao Sim) |
| T2.2b | Fenótipo → corpo modular: partes, genes adormecidos com limiar funcional, efeitos (velocidade, degrau, escalar, cavar, armadura, pelo), custo Kleiber, dimorfismo por sexo; visual voxel montado | T2.2a | `BodyPlan` + visual | testes de custo (nenhum traço grátis) |
| T2.2c | Ciclo de vida: longevidade em anos, maturidade, ninhada, intervalo, gestação/gema, crescimento, senescência (Gompertz), estação reprodutiva, hibernação/torpor | T2.2a, T2.1c | genes + fisiologia | testes analíticos (curva de sobrevivência Gompertz) · **⏳ 2026-11-27: Gompertz + crescimento até a maturidade (5 testes); faltam ninhada, gestação, estação reprodutiva e torpor** |
| T2.2d | Acuidade dos sentidos por gene (alcance e ângulo da visão, visão noturna, olfato, audição), com custo | T2.2a, T2.0a | genes + Senses | testes |
| T2.2e | Motivações como genes (D3): pesos da recompensa por indivíduo | D3, T2.2a | genes + `reward.py` | teste: pesos diferentes → recompensas diferentes · **✅ lado Python 2026-11-27** (`compute_totals(..., motivations)`; falta vir do genoma via protocolo v3) |
| **T2.3** | **Protocolo v3**: + genes expressos (vetor fixo do catálogo), fotoperíodo e tendência, sinais sociais por setor (parente, sexo, maduro, filhote), pesos de motivação; ações acasalar e alimentar ativas | T2.1c, T2.2b–e | `contracts/protocol-v3.json` + fixture + codecs C#/Py | testes de round-trip nos dois lados |
| **T2.4** | **Reprodução e população** | | | |
| T2.4a | Acasalar (condições do plano), gestação ou ovo, nascimento junto da mãe, alimentar filhote, reconhecimento de parente pelo cheiro de família | T2.3 | Core + testes | teste: casal compatível gera filho com genes dos dois |
| T2.4b | População por nascimentos (substitui o respawn); arquivo anti-extinção (resgate abaixo de N vivas), teto; espécies iniciais (D1) | T2.4a, D1 | Sim | run de 2 anos simulados sem extinção total |
| **T2.5** | **Predadores**: dieta por gene (herbívoro, carnívoro, onívoro), mordida em criatura (dano por presas, defesa da carapaça), carcaça vira carne que apodrece, carniceiros, medo real calibrado | T2.4b, D1 | Core + testes | predador e presa coexistem ≥ 5 anos simulados (com resgate) |
| **T2.6** | **Cérebro da etapa 2** | | | |
| T2.6a | Instinto por indivíduo (~3k pesos, entrada = observação reduzida, `ação = base + α(idade)·instinto`), herdado com mutação | T2.3, T2.2a | `learning/instinct.py` + testes | teste: filho herda instinto mutado; α cai com a idade |
| T2.6b | Memória longa: janelas de 256–512 passos dentro dos 6 GB | T2.0e | config + medição de VRAM | cabe na 2060; avaliação ≥ janela de 64 |
| **T2.7** | **Genealogia e Observatório** | | | |
| T2.7a | Genealogia em SQLite (append-only, escrita pelo Sim): nascimento, morte e causa, pais, alelos, fenótipo, espécie, geração; controle de tamanho | T2.4a | schema + testes | 1 ano simulado gravado e consultável |
| T2.7b | Observatório no Godot (Tab: Espécies, Genes, Genealogia) + inspetor (abas Genes e Família); agrupamento de espécies | T2.7a, T2.0d | UI | screenshots conferidos |
| **T2.8** | **Controles científicos:** run de controle sem seleção (sobrevivência sorteada, mesma mutação) para provar que a mudança de genes vem da seleção | T2.4b | script + relatório | diferença significativa nos genes-alvo entre seleção e controle |
| **T2.9** | **Avaliação da etapa 2** | T2.5, T2.6, T2.7, T2.8 | relatório | reprodução sustentada; mudança direcional de genes × controle; sinal Baldwin (tempo até a 1ª refeição cai por geração); r/K (A com mais gerações que B); nascimentos concentrados na estação. **Parar e mostrar.** |
