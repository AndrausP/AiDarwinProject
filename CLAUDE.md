# Criatura IA

## Objetivo
Criaturas que **evoluem com o tempo, como na teoria de Darwin**, num mundo todo em blocos (voxel).
Cada animal vive como animal de verdade (anda, come, bebe, dorme, foge, se reproduz); o **núcleo da
vida é preservar a própria vida**. A população se automodifica: filhos nascem com variações, a
seleção natural decide quem passa os genes, e comportamentos e corpos novos surgem conforme o mundo muda.

Roteiro com dependências: `docs/ROADMAP.md`. Plano detalhado: `docs/tasks/002-voxel-evolucao.md` (etapas 1 → 2 → 3 → 4). Histórico: `docs/tasks/001-fases-1-2.md`, `PLANO.md`.

## Regras do projeto (não negociáveis)

### Darwin
- **Só genes são herdados.** Nada aprendido em vida passa geneticamente. Memória do cérebro (GRU) zera ao nascer.
- Cultura (hábitos) passa só por **imitação** de parentes (etapa 3), nunca por cópia de pesos.
- Variação vem de **crossover + mutação** (diploide mendeliana, mutações tendem a recessivas); taxa de mutação é gene.
- **Todo traço tem custo** (metabólico ∝ massa e partes, Kleiber). Não existe supercriatura.
- Especiação, dimorfismo, sistema de acasalamento e estratégias r/K **emergem**; nunca são programados.
- Ciclo de vida é gene: longevidade em anos, idade mínima de reprodução, ninhada, estação reprodutiva,
  senescência. Comportamentos de longo prazo (migração, hibernação) surgem por **instinto herdado + cultura**,
  com pistas reais do mundo (fotoperíodo, clima regional), nunca por script.
- Mundo-alvo (etapa 2): ilha grande sem borda, oceano salgado, montanhas, rios, estações por região; ano de 12 dias.

### Comportamento é aprendido, não programado
- Proibido script de comportamento ("se sede, vá à água", "fuja de carnívoro"). O cérebro só recebe a
  **observação pública** (protocolo) e aprende pela recompensa homeostática.
- Recompensa = aproximar fisiologia dos setpoints − dor − medo − morte (+ curiosidade pequena). Usa só interocepção e eventos.
- O cérebro nunca recebe posição de recursos nem estado privilegiado.
- **Locomoção:** o cérebro decide quando/para onde/quão rápido andar, pular, nadar, escalar e cavar. O **corpo**
  (patas, tamanho, garras, velocidade) evolui por genes (etapa 2) e muda o *efeito* das ações; o cérebro vê
  os genes expressos e se adapta ao próprio corpo. Mesmo vetor de ações para todos os corpos.

### Cérebro
- v3 (`--brain gru`, padrão): ~4,0 M parâmetros — encoder 1130→768→768, **GRU 512 (memória)**, head com atalho
  reativo, ator 13 / crítico 1. Curiosidade RND existe mas fica **desligada** (T1.1: distraía e fazia cavar). PPO recorrente (replay de 64 passos, reset no nascimento).
- v2 (`--brain mlp`): 0,65 M, sem memória. Mantido só para comparação e para retomar runs antigos (`v2-ppo-a`).
- Etapa 2 adiciona camada de **instinto** por indivíduo (herdada com mutação) e genes como entrada.
- **Meta do projeto: cérebro v4 ~30 M parâmetros** (codificador por sentido, atenção social, GRU 2×1024,
  mapa mental espacial). Cresce em degraus a partir do v3, cada degrau só com ganho medido na seed 0.
  Desenho em `docs/tasks/002-voxel-evolucao.md`.

### Ambiente de treino (padrão)
- Treino roda no **simulador próprio em C# puro (`CreatureIA.Sim`)**, sem Godot, vários mundos em paralelo num
  STEP só (`scripts/train.ps1`). Godot é só para assistir. `train-ppo.ps1` (Godot headless) fica para comparação.
- Máquina principal de treino: **desktop RTX 2060 6 GB** (casa, 24 h). Notebook RTX 3050 Laptop: desenvolvimento.
- Precisão mista **fp16** (Turing não tem bf16). Nuvem grátis só como plano B.

- **Sem pesos pré-treinados** (LLMs ou outros): o cérebro nasce em branco; conhecimento vem só de evolução + aprendizado em vida.

### Evidência
- Só declarar PASS com saída real de `dotnet test` / `pytest` / run. Nunca converter "não executado" em aprovação.
- Melhora entre blocos de treino em mapas diferentes **não é evidência**. Avaliação = **seed 0 reservada**
  (nunca treinada) contra o aleatório na mesma seed.
- Cada etapa fecha com avaliação; **parar e mostrar ao usuário no fim de cada etapa**.
- Mudança no layout de observação/ações = **nova versão de protocolo** (`contracts/protocol-vN.json` + fixture) e retreino do zero.

### Engenharia
- `CreatureIA.Core` é C# puro e testável (voxel, fisiologia, medo); Godot só integra e apresenta. Nada de física do Godot: colisão é `VoxelPhysics`, visão é DDA.
- Headless não cria nada visual. Tudo determinístico por seed (tempo simulado, nunca relógio de parede).
- Checkpoints atômicos e retomáveis (mesmo `-RunName` continua). A máquina hiberna às 17:30 (Brasília) nos dias úteis.
- Testes C#: NUnit, `Should_[Comportamento]_When_[Condição]`. Python: pytest.
- Commits: Conventional Commits (`feat:`, `fix:`, `refactor:`, `test:`).
- **Encoding:** nunca reescrever arquivo com `Get-Content`/`Set-Content` do PowerShell 5.1 (corrompe UTF-8 sem BOM). Use Edit/Write ou `[IO.File]` com `UTF8Encoding($false)`.
- Docs e mensagens ao usuário em português.

## Comandos
```powershell
# testes
$env:DOTNET_ROOT="$PWD\.tools\dotnet8"; $env:PATH="$PWD\.tools\dotnet8;$env:PATH"
dotnet test godot\tests\CreatureIA.Tests
cd brain; ..\.venv\Scripts\python.exe -m pytest -q
# compilar o jogo (depois de mudar C#)
.tools\godot-4.7.2-mono\...\Godot_v4.7.2-stable_mono_win64_console.exe --headless --path godot --build-solutions --quit
# treinar no Sim (retomável pelo RunName: cérebro e mundos); -MaxMinutes para parar a tempo
.\scripts\train.ps1 -RunName v3-a -Brain gru -Worlds 6 -Ticks 72000 -Seed 1 -Plants 2800 -Lakes 12
# avaliar a etapa (seed 0 reservada, políticas congeladas contra o aleatório)
.\scripts\eval-stage.ps1 -Policies "v2=night-v2:mlp","v3=night-v3:gru" -Replicas 6 -Sequential
# etapa 2: seleção + controle lado a lado, depois relatório (T2.8/T2.9)
.\scripts\stage2.ps1 -Name s2-a -Ticks 288000 -Worlds 4
# assistir um run do Sim (Tab = observatório) / sala branca de exemplos
.\scripts\watch.ps1 -Sim s2-a-sel
.\scripts\watch.ps1 -Fresh
.\scripts\watch.ps1 -Showcase
```

## Memória do projeto
- Erros e lições: `docs/knowledge/errors-aprendidos.md` (sintoma → causa → correção).
- Contratos: `contracts/`. Resultados de runs: `benchmarks/runs/<run>/`.
