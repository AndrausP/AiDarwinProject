# Task 002 — Mundo voxel, cérebro em três camadas e evolução darwiniana

**Status:** etapa 1 em fechamento (iniciada 2026-11-26) — motor voxel, protocolo v2, cérebros v2 e v3 prontos; treinos parados a pedido do usuário  
**Base:** Task 001 (PPO `ppo-f`: come 3,5× o aleatório, vida 3,16×, sede não resolvida)

> Este arquivo é o **registro das decisões de design**. O trabalho executável está dividido em tasks com
> dependências: `003-etapa-1-fechamento.md`, `004-etapa-2.md`, `005-etapa-3.md`,
> `006-etapa-4-e-transversais.md`. Visão geral: `docs/ROADMAP.md`. Onde este arquivo e as tasks
> divergirem, valem as tasks (decisões mais novas).

## Objetivo
Animais que vivem como animais reais — andam, comem, bebem, descansam, fogem, se reproduzem — num
mundo todo em blocos, e cuja população **se automodifica**: cada filho nasce com características
novas, a seleção natural decide quem passa os genes, e comportamentos (cavar, escalar, bando, hábito
noturno, preferência de habitat) surgem conforme o mundo muda. **Núcleo da vida: preservar a própria
vida**; o resto são meios que os animais encontram.

## Decisões fechadas

| Tema | Decisão |
|---|---|
| Cérebro | **3 camadas:** base compartilhada (espécie, PPO, recebe os genes como entrada) + **instinto** (~3k pesos, herdado com mutação, não aprendido) + **hábitos** (camada pessoal que nasce zerada, aprende na vida e por imitação) |
| Herança | Darwin: genes + instinto mutados; nada aprendido é herdado geneticamente. Hábitos passam **culturalmente** (filho imita adultos da família que estão indo bem) |
| Reprodução | **Sexuada:** dois adultos < 2 m, ação acasalar, energia > limiar fértil (gene), distância genética < limiar (especiação). Filho = crossover + mutação; cada pai paga gema |
| População | Treino: resgate pelo arquivo abaixo de 16 vivas, ~~teto 64~~ **teto 150–250 por mundo (ilha, etapa 2)**, velhice (gene). Depois: **modo "realidade dura"** configurável (sem resgate) |
| Predadores | ~~Duas espécies iniciais~~ **Espécies iniciais em aberto (D1 em `004-etapa-2.md`; recomendação: A herbívoro 3 anos, B herbívoro 20 anos, C carnívoro)**: herbívoros (maioria) + carnívoros (minoria). Mordida em criatura fere; carcaça vira carne. Dieta, tamanho, garras e velocidade evoluem livres |
| Habilidades | Cavar e escalar existem para todos, mas a eficácia depende do corpo (garras, força, tamanho). A evolução escolhe o corpo, o aprendizado escolhe quando usar |
| Medo | **Sentido** (vê/cheira carnívoros e maiores que si) + **sentimento** na interocepção (cresce com ameaça próxima e dor recente, a recompensa pune) + **gene** do peso do medo. Nada de "fuja" programado |
| Voxel | **Tudo em blocos, inclusive a física:** terreno em chunks com colisão de blocos, cavável; água, árvores escaláveis e animais em cubos; visão por raio voxel (DDA em C#, sem física) |
| Escala | Blocos de **0,5 m**. Etapa 1: mapa **384 × 384 m**, 48 m de altura. **Etapa 2: ilha em mapa de 768 × 768 m, 64 m de altura.** Animais de ~0,5 m a ~3 m (gene). Seed de avaliação reservada |
| Genética (dono) | ~~Python dono dos genomas~~ **em aberto (D2): recomendação C# Core**, porque o simulador próprio roda tudo localmente |

## Evolução física e espécies (grill-me 2026-11-26, parte 2)

| Tema | Decisão |
|---|---|
| Corpo | **Plano modular:** partes com presença e tamanho por gene — nº de patas (2/4/6), comprimento das patas, garras, dentes/presas, chifres, carapaça, pelagem, orelhas, cauda, olhos (tamanho/posição), cor e padrão. Efeito funcional por fórmula (patas longas = rápido e caro; garras = escalar/cavar; carapaça = defesa e lentidão; pelagem = frio). Montado em voxel |
| Novidade | **Genes adormecidos:** todo genoma carrega o catálogo inteiro; partes começam em zero e mutação as faz crescer ao longo de gerações. Limiar funcional: a parte só funciona acima de um tamanho mínimo. Inclui nadadeiras (nicho aquático), veneno, camuflagem. **Sem voo** |
| Genética | **Diploide mendeliana:** 2 alelos por gene (1 de cada pai, sorteado), cada um com valor + dominância; fenótipo = média ponderada pela dominância. Mutações novas tendem a ser recessivas (se espalham escondidas e reaparecem quando dois portadores cruzam) |
| Especiação | Viabilidade do filho cai suavemente com a distância genética dos pais + **genes de preferência de parceiro** (seleção sexual). Isolamento por geografia/bioma acelera. Espécies detectadas por agrupamento genético **só para exibir e medir** |
| Sexos | Cromossomo sexual (tipo XY); **modificador por sexo em cada gene de corpo** → dimorfismo evolui (macho colorido, fêmea maior…) |
| Sistema de acasalamento | Evolutivo: genes de investimento (gema/gestação), cuidado parental, vínculo de casal e seletividade → monogamia, poligamia, cuidado só materno etc. surgem conforme compensa. Exibição (cor + som) como sinal de corte |
| Ciclo de vida | Filhote nasce pequeno (proporcional à gema), frágil, **cresce** até adulto (taxa de crescimento e maturidade são genes). Ação nova **alimentar** (passar energia a filhote próximo). Reconhecimento de parentes por cheiro de família. Estratégias r/K emergem |

Decisões técnicas tomadas pelo agente (revisáveis):
- Todo traço tem custo (manutenção metabólica ∝ massa e partes; Kleiber) → não existe "supercriatura".
- Taxa de mutação é gene (auto-adaptativa, plano 8.1).
- Mesmo vetor de ações para todos os corpos; o corpo muda o efeito das ações, e o cérebro base vê os genes expressos.

## HUD e visualização (grill-me 2026-11-26, parte 3)

| Tema | Decisão |
|---|---|
| Onde | **Tudo dentro do jogo** (Godot) |
| Histórico | **Completo:** o Python (dono dos genomas) grava a genealogia em **SQLite** (nascimento, morte e causa, pais, alelos, fenótipo, sexo, espécie, geração). O jogo carrega ao abrir, então o treino headless da noite aparece; depois segue ao vivo |
| Layout | **HUD leve** (barra superior: dia/hora/estação, vivas, nº de espécies, geração) + **inspetor lateral** ao clicar + **Observatório** em tela cheia com **Tab** (abas Espécies, Genes, Genealogia), com o mundo rodando atrás |
| Genealogia | Dois modos: **Família** (centrada na selecionada: avós, pais, irmãos, filhos, netos; vivo/morto e causa; clicar navega) e **Vida** (filogenia das espécies: ramifica ao surgir, termina na extinção, eixo em gerações) |
| Espécies | **Cartões** (nome gerado, retrato voxel de macho e fêmea, população, dieta, sistema de acasalamento observado, hábitat, traços marcantes, ancestral, geração de origem) + **gráfico de áreas** da população no tempo. Clicar pinta no mapa e leva a câmera |
| Genes | **Linhas por espécie** (média por geração + faixa de variação), escolha de genes, opção de **separar por sexo** (dimorfismo), marcador quando uma parte nova cruza o limiar funcional |
| Inspetor | Cabeçalho (retrato voxel, nº, sexo, espécie, fase de vida, geração) + **4 abas:** Estado (fisiologia, medo, dor, ação atual), Genes (fenótipo + 2 alelos, dominante/recessivo, adormecidos crescendo), Família (pais, parceiro, filhos; abre a Genealogia), Mente (ação atual e quanto veio de base / instinto / hábitos) |

Encaixe nas etapas: HUD leve, inspetor (Estado, Mente) → etapa 1; SQLite, Observatório, abas Genes/Família do inspetor → etapa 2; métricas de cultura/emergência → etapa 3.

## Etapas (cada uma fecha com avaliação no mapa reservado contra o aleatório na mesma seed)

### Etapa 1 — Mundo voxel + cérebro base
- Motor voxel em C# (chunks, malha gulosa, colisão por chunk, edição de bloco), biomas em blocos
  (grama, terra, areia, pedra, neve, água), árvores de blocos, animais de cubinhos animados.
- Visão voxel DDA (tipo de bloco por raio) + criaturas; protocolo **v2** (observação nova, ações
  12: + cavar, escalar).
- Fisiologia herdada da Task 001 (sono, nascimento, plantas realocáveis).
- Medo: sentido + sentimento na interocepção.
- **Critério:** vida ≥ 3×, comer com fome ≥ 2×, beber com sede ≥ 2×, dormir com fadiga alta;
  autopreservação em primeiro lugar.

#### Progresso etapa 1 (2026-11-26)
| Sub-etapa | Estado | Evidência |
|---|---|---|
| 1a Núcleo voxel (`CreatureIA.Core/Voxel`): grade em chunks 16³ preguiçosa, gerador (biomas, 6 lagos, árvores), malha gulosa, raio DDA, colisão AABB com degrau 0,55 m, cavar | ✅ | 26 testes em `VoxelTests`; mundo cheio: gerar 282 ms, malha 1,6 s (Release), raio ~1,6 µs |
| 1b Godot no voxel: `BiomeMap` dono do `VoxelWorld`, chunks renderizados, criatura `Node3D` + `VoxelPhysics` (nada de física Godot), visão DDA + cilindros, picking voxel | ✅ | smoke aleatório `benchmarks/runs/voxel-smoke-2026-11-26` (48 vivas, 0 falhas, ~15 ms/tick); screenshot conferido |
| 1c Protocolo v2 (1000 obs / 12 ações: + cavar, escalar), medo (sentido: visão/cheiro de ameaça; sentimento: interocepção 11; recompensa `fear_weight=0.3`) | ✅ | `contracts/protocol-v2.json`, fixture `hello-v2.hex`; C# 43/43, pytest 47/47; smoke PPO `v2-ppo-smoke-2026-11-26` (3000 ticks, 0 falhas) |
| 1d Treino do cérebro base do zero | ✅ | `benchmarks/runs/v2-ppo-a` (3 mundos, seeds 1–3, currículo 2800 plantas / 12 lagos, 72k ticks concluídos); vida média 523 → 708 s |
| 1d' **Cérebro v3** (decisão do usuário 2026-11-26): GRU 512 de memória + curiosidade RND, 3.991.065 parâmetros; PPO recorrente | ✅ código / ⚠️ treino parado na atualização 1.367, vida média caindo 474 → 392 s (T1.1) | `learning/recurrent.py`, `test_recurrent.py`; pytest 55/55; smoke `v3-smoke`; treino `v3-gru-a` (mesma config do `v2-ppo-a`) |
| 1e Avaliação no mapa reservado (seed 0): aleatório × v2 (`v2-ppo-a`, 72k ticks concluído) × v3 (`v3-gru-a`) | ⬜ | critério acima |
| Vegetação variada: carvalho, carvalho grande, bétula, abeto, **sequoia gigante** (20–32 m, tronco 2×2/3×3), acácia, cacto; arbustos; capim e flores (decoração em X, sem colisão, invisível à visão) | ✅ | `TreeBuilder`; 46/46 testes; screenshot seed 1337 |
| Criaturas em cubos (visual provisório; corpo modular voxel por gene na etapa 2) | ✅ | `CreatureVisual` usa só caixas |
| HUD leve + inspetor (Estado, Mente) | ⬜ | |

Notas técnicas:
- Mapa 384 m → plantas 700 → **1400** (mesma densidade); spawn em terra seca a ±120 m.
- Olfato de plantas agora soma só células até 40 m (antes: todas as plantas); contribuição além disso < 1/1600.
- Ameaça = carnívoro (1) ou tamanho relativo > 1,15; em etapa 1 todos têm tamanho 1 e são herbívoros, então medo vem só da dor até a etapa 2.

### Etapa 2 — ampliações aprovadas pelo usuário (2026-11-26)

**Mundo: ilha grande, sem borda**
- Mapa de oceano com uma ilha grande no meio: costa, praias, planícies, florestas, savana, deserto no
  lado seco (sombra de chuva), **cordilheira central nevada**, **rios** nascendo nas montanhas e descendo até
  o mar, lagos. Tamanho configurável: alvo 768 × 768 m (ilha ~600 m), 64 m de altura; recuar para 512 m se o
  desempenho pedir.
- **Mar é água salgada** (bloco próprio): não mata a sede. Rios e lagos são água doce, então secas e
  estações empurram os animais para os rios.
- Sem parede: o oceano é a fronteira natural (nadar cansa; mar aberto mata por exaustão/frio).
- Clima por região: temperatura por latitude e altitude, umidade por distância do mar e sombra de chuva.

**Tempo**
- Dia 10 min (igual). **Ano 12 dias = 2 h simuladas** (era 8 dias); estação = 3 dias.
- **Estações por região:** cada bioma tem seu pico de produção (savana: chuva/seca; norte: verão curto e
  inverno rigoroso; floresta: estável). Na seca alguns lagos rasos secam.
- **Fotoperíodo nos sentidos** (duração do dia e sua tendência) como pista de estação. Entra no protocolo v3
  junto com os genes.

**Ciclo de vida (genes, com custo)**
- **Longevidade em anos (gene):** espécie inicial A (herbívoro pequeno, estrategista r) ≈ **3 anos**;
  espécie B (grande, estrategista K) ≈ **20 anos**. Faixa evolutiva 1 a 25 anos.
- **Idade mínima de reprodução (gene):** A ≈ 0,4 ano; B ≈ 4 anos.
- **Taxa reprodutiva (genes):** ninhada (A 3–6 / B 1), intervalo entre crias, gestação/gema, cuidado parental.
- **Estação reprodutiva:** cio disparado pelo fotoperíodo (gene de qual época).
- **Senescência:** mortalidade cresce com a idade (Gompertz) e a fertilidade cai no fim da vida.
- **Viver mais custa:** longevidade aumenta o gasto de manutenção (reparo); tamanho grande vive mais e é mais
  lento (Kleiber). Daí o trade-off r/K emerge em vez de todo mundo virar "vive 25 anos".
- Teto de população sobe (alvo 150–250 por mundo, medido pelo desempenho).

**Cérebro — meta do projeto: ~30 M parâmetros (decisão do usuário 2026-11-26)**
- v3 (atual, 4 M) é o ponto de partida; cresce em degraus até o **v4 ~30 M** conforme cada degrau provar
  ganho na avaliação da seed 0 (nunca crescer sem evidência).
- Desenho alvo do v4 (a validar por degrau):

  | Módulo | ~Parâmetros | Para quê |
  |---|---|---|
  | Codificador por sentido (visão como grade de raios com convolução 1D; olfato/som/corpo em MLPs) | ~4 M | ler o mundo |
  | Módulo social: atenção sobre as criaturas vistas (parente, sexo, tamanho, dieta, distância) | ~3 M | bando, hierarquia, corte, caça em grupo |
  | Memória recorrente: GRU 2 camadas × 1024 | ~12 M | rotinas, lembrar lugares e eventos da vida |
  | Memória espacial (mapa mental chave-valor: "onde vi comida/água/perigo, em que estação") | ~8 M | migração aprendida, voltar ao rio na seca |
  | Cabeças ator/crítico + previsão auxiliar | ~2–3 M | decidir e estimar o futuro |

  Instinto por indivíduo (etapa 2) e hábitos (etapa 3) ficam fora dessa conta.
- Memória treinada em janelas mais longas (256–512 passos); precisão mista fp16 (Turing não tem bf16).
- Crescimento sem jogar fora o aprendido: o degrau maior começa imitando o menor (destilação) e depois
  segue no PPO.

**Hardware**
- **Máquina principal de treino: desktop com RTX 2060 6 GB** (casa, pode ficar ligada 24 h).
  Notebook RTX 3050 Laptop 6 GB: desenvolvimento e testes.
- 6 GB de VRAM bastam para 30 M parâmetros (pesos + Adam ≈ 0,5 GB); o limite é a memória de ativações
  do treino em sequência → minibatches por slot e janelas de memória dimensionadas para caber.

**Sentidos e metabolismo (genes)**
- **Acuidade por gene:** alcance/ângulo da visão, visão noturna, olfato, audição (cada um com custo).
- **Hibernação/torpor:** modo de metabolismo muito baixo durante o sono longo, com temperatura corporal
  caindo; gene de limiar. Combinado com cavar toca e estações, permite "dormir o inverno".

**Riscos novos**
- Mundo 4× maior e população 4× maior: tempo por tick sobe; precisa de malha por distância na janela de
  visualização e compressão de chunks uniformes (só pedra/água).
- Espécie de 20 anos evolui ~8× mais devagar que a de 3 anos (como na natureza).

**Infraestrutura de treino (início da etapa 2) — padrão escolhido pelo usuário: ambiente próprio**
- **Simulador de treino próprio em C# puro (`CreatureIA.Sim`) é o ambiente padrão de treino**, sem Godot: usa `CreatureIA.Core` (voxel,
  física, fisiologia, medo), roda vários mundos em paralelo nos núcleos da CPU e entrega observações em lote.
  O Godot fica só para assistir. Meta: 5–10× mais passos por segundo (a medir). Exige mover para o Core o que
  hoje está nos scripts Godot (sentidos, plantas, criatura); paridade verificada por testes (mesma seed →
  mesma observação nos dois).
- **Cérebro mais barato por passo:** precisão mista (fp16/bf16) e GRU fundido (cuDNN) no treino. Meta ~2×.
- **Ponte em memória compartilhada** em vez de TCP quando simulador e cérebro estão na mesma máquina.
- Nuvem grátis (Kaggle/Colab/Oracle) **não é o padrão** (usuário preferiu ambiente próprio); fica só como
  plano B. Checkpoints retomáveis permitem alternar máquinas (notebook ↔ desktop).
- **Sem pesos pré-treinados:** o cérebro nasce em branco por regra (Darwin). Modelos prontos (LLMs etc.)
  trazem conhecimento que a natureza não deu e não servem de cérebro de criatura.

### Etapa 2 — Genes, instinto, reprodução, predadores
Itens originais (genoma, instinto, predadores, genealogia, sinal Baldwin) somados às ampliações acima:
**tasks executáveis em `004-etapa-2.md`** (T2.0–T2.9, com dependências e decisões pendentes D1–D4).

### Etapa 3 — Cultura, cavar/escalar e realidade dura
- Camada de hábitos por indivíduo + imitação de parentes na infância.
- Cavar/escalar com eficácia por gene; métricas de emergência (bando, fuga, toca, hábito noturno,
  especiação, nicho).
- Modo "realidade dura" (sem resgate).
- **Territorialidade:** ação/gene de marcar cheiro no chão (decai com o tempo); outros sentem a marca de
  estranhos. Base para territórios e hierarquia.
- **Ação "colocar bloco":** ninhos, muros, diques (castor). Custo de energia; só blocos que o animal carrega
  (terra/folhas cavadas). Vigiar exploits (muralhas perfeitas).

### Etapa 4 — Ecologia avançada (depois da etapa 3)
- **Doenças e parasitas:** contágio por proximidade/carcaça, genes de imunidade com custo, epidemias e
  seleção por resistência. Opcional, só depois das etapas 1–3 estáveis.
- Candidatos: plantas que evoluem (coevolução com herbívoros), polinização, guardar comida (ação carregar).

## Execução
- Autônoma dentro de cada etapa; **parar e mostrar no fim de cada etapa**.
- Janela de trabalho: até 17:30 (Brasília) nos dias úteis; depois a máquina hiberna e continua em casa.
- Treinos longos em segundo plano exigem iniciar o Claude Code com
  `CLAUDE_CODE_DISABLE_BG_SHELL_PRESSURE_REAP=1`; sem isso, blocos de até 10 min em primeiro plano.
- Checkpoints atômicos; tudo retomável depois da hibernação.

## Riscos
- Motor voxel próprio é a maior peça nova (malha + colisão + edição): desempenho a medir cedo.
- Mudança para o protocolo v2 invalida o cérebro atual: o cérebro base é retreinado do zero.
- Medo como punição pode gerar criaturas paralisadas de medo: o peso do medo é gene, mas a calibragem inicial precisa ser medida.
- Carnívoros podem extinguir herbívoros (ou o contrário) cedo: o resgate do arquivo cobre o treino.
