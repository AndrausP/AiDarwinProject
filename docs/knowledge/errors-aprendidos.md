# Erros aprendidos

Cada entrada: sintoma → causa verificada → correção. Data da descoberta entre parênteses.

## Godot / C#

### `Engine.TimeScale` não acelera a simulação (2026-11-26)
- **Sintoma:** lockstep a ~4.3 Hz tanto com `TimeScale=1` quanto `4`; ~100 ms ociosos por tick cerebral.
- **Causa:** no Godot 4, `Engine.TimeScale` só multiplica o `delta` de cada passo; o número de passos físicos por segundo real continua `physics_ticks_per_second`. Pior: a fisiologia recebia `dt = 4/60`, violando o contrato de passo fixo `1/60 s`.
- **Correção:** `WorldRoot.ApplyTimeScale` escala `Engine.PhysicsTicksPerSecond` junto (delta segue exatamente 1/60). Para treino headless, rodar com `--fixed-fps 60` (sem pacing de relógio real; `run-headless.ps1 -TimeScale 0`, padrão).

### Tooling C# do Godot 4.7 não carrega com SDK .NET 11 RC (2026-11-26)
- **Sintoma:** `--editor` headless: `Could not load file or assembly 'System.Runtime, Version=11.0.0.0'` em `GodotTools.ProjectEditor.ProjectUtils.Open`.
- **Correção:** SDK .NET 8 portátil em `.tools/dotnet8`; exportar `DOTNET_ROOT`, `DOTNET_MULTILEVEL_LOOKUP=0` e PATH antes de chamar o Godot (feito em `run-headless.ps1`).

### Chave de propriedade exportada em `.tscn` precisa do nome C# (2026-11-26)
- **Sintoma:** `NullReferenceException` em `WorldRoot._Ready` (linha do `Instantiate`).
- **Causa:** `World.tscn` tinha `creature_scene = ...`; em Godot 4 C# a chave serializada é o nome da propriedade (`CreatureScene`).
- **Correção:** chave renomeada. Ao editar `.tscn` à mão, usar PascalCase para `[Export]` C#.

### Projeto Godot sem `ImplicitUsings`/`Nullable` (2026-11-26)
- **Sintoma:** 32 erros `CS0246` (`List<>`, `Task<>`...). O registro de 2026-11-25 dizia "compilação estática PASS" — estava errado.
- **Correção:** `<ImplicitUsings>enable</ImplicitUsings>` e `<Nullable>enable</Nullable>` em `CreatureIA.Godot.csproj`. Lição: só declarar PASS com saída de `dotnet build` real.

### `CS0017` no projeto de testes (2026-11-26)
- **Causa:** `ManualTestRunner.Main` + entry point gerado pelo `Microsoft.NET.Test.Sdk`.
- **Correção:** `<GenerateProgramFile>false</GenerateProgramFile>`.

### `BrainBridge.Connected` verdadeiro antes do handshake (2026-11-26)
- **Sintoma:** `Lockstep exchange failed at tick 0: Bridge is not connected.`
- **Causa:** `Connected` olhava só o socket TCP; o coordenador podia mandar STEP antes de HELLO/CONFIG (viola `Disconnected → Hello → Configured → Running`).
- **Correção:** `Connected` exige `Configuration` recebida e stream pronto.

### Som expirava por relógio real (2026-11-26)
- **Causa:** `SensoryField` usava `Time.GetTicksUsec()` (500 ms de parede) e empilhava um pulso novo por criatura a cada tick físico.
- **Efeito:** audição dependia da velocidade da simulação (não determinística) e saturava somando centenas de pulsos duplicados.
- **Correção:** um pulso ativo por fonte, expiração em 30 ticks físicos simulados (`SoundLifetimePhysicsFrames`).

### Interop por raio domina o custo de visão (2026-11-26)
- **Causa:** por raio: `PhysicsRayQueryParameters3D.Create` + `new Array<Rid>` + chaves string no `Dictionary` + `IsInGroup`/`CollisionLayer` por hit; `GetChildren()` aninhado no olfato.
- **Correção:** query reutilizada por criatura, chaves `Variant` cacheadas, classificação cacheada por `collider_id`, snapshot de posições por frame físico, lagos cacheados. Visão 77→~10 ms/tick; olfato+audição 20→~2.5 ms/tick (48 criaturas).

## Ferramentas / Windows

### PowerShell 5.1 corrompe UTF-8 sem BOM ao reescrever arquivo (2026-11-26)
- **Sintoma:** HUD mostrando `cÃ©rebro`, `prÃ³xima`; comentário `Ã—` no lugar de `×`.
- **Causa:** `Get-Content -Raw` lê arquivo UTF-8 sem BOM como ANSI (1252); `Set-Content -Encoding utf8` grava o texto já errado → dupla codificação.
- **Correção:** reverter com `UTF8.GetString(1252.GetBytes(texto))`. Para editar por script usar `[IO.File]::ReadAllText` / `WriteAllText` com `UTF8Encoding($false)`, ou a ferramenta Edit.

## Voxel (Task 002)

### Winding de face no Godot
- Godot trata **horário** (visto de fora) como frente. A malha gulosa gera cantos anti-horários a partir da normal e emite índices `(0,2,1),(0,3,2)`. Teste de guarda: `Should_WindClockwiseFromOutside_When_MeshingTopFace`.

## Python

### `--policy ppo` quebrava no primeiro STEP (2026-11-26)
- **Causa:** servidor chamava `policy.actions(ids, obs)`; `OnlinePPOPolicy` só expõe `act(step)` (precisa dos eventos DEATH). Nenhum teste cobria o servidor.
- **Correção:** protocolo `Policy.act(step)`; políticas sem estado adaptadas. Testes em `brain/tests/test_server.py` (TCP real, inclusive `OnlinePPOPolicy`).

### Harness travava Godot com stdout em PIPE não drenado (2026-11-26)
- **Sintoma:** zero ticks, Godot mudo.
- **Correção:** redirecionar saída do Godot para arquivo em runners longos.

## Calibração do mundo (2026-11-26)

### Mundo começava à meia-noite no auge do inverno
- **Causa:** `TimeOfDay01 = 0` e `YearProgress01 = 0` → −8 °C (noite) −10 °C (inverno) no ambiente desde o tick 0.
- **Correção:** início às ~07:12 da primavera (`0.3` / `0.25`).

### Dia/noite e estações avançavam no relógio de frame
- **Causa:** `_Process` continuava rodando durante a espera do lockstep → tempo do mundo dependia da latência do Python (não determinístico).
- **Correção:** `_PhysicsProcess` (pausa junto com a árvore).

### Frio letal em todo bioma
- **Evidência:** random, 10 min sim, respawn: 231 mortes por frio, 5 por fome (`benchmarks/runs/calib-spring-2026-11-26b`).
- **Causa:** com `HeatExchangeRate = 0.02`, tremor só sustenta `T_amb + 6 °C`; nenhum lugar ficava acima de 30 °C vital → nada a aprender.
- **Correção:** `0.005` (sustenta `T_amb + 24 °C`). Resultado: 31 mortes, todas por fome, mediana 463 s vs ~410 s previsto (dentro de ±20%) (`calib-heat005-2026-11-26`). Testes de guarda em `PhysiologyEngineTests`.

### Conflito de porta com sessão espectadora
- **Sintoma:** run headless conectou ao servidor da janela aberta (porta 5555) e gravou no arquivo errado.
- **Correção:** `CREATURE_IA_BRAIN_PORT`; `scripts/train-ppo.ps1` usa 5557.

## Treino PPO (2026-11-26, noite)

### Defeitos que impediam o aprendizado (todos corrigidos, com testes)
- **Buffer por ID:** cada morte forçava update de 1 passo → buffer por *vagas* fixas (auto-reset estilo env vetorizado), linhas de padding mascaradas.
- **Ação reconstruída com `atanh`:** com std até e² a `tanh` satura e a razão PPO vira ruído (entropia subia) → guardar a ação crua; teto `log_std ≤ 0`.
- **Sem normalização:** temperatura ~37 contra features 0..1; recompensa ~1e-4/passo → `RunningMeanStd` de observação e escala de retorno (salvos no checkpoint).
- **Plantas invisíveis:** colisor esfera r=0,38 com topo em 1,08 m; raio do olho em 1,1 m passava por cima → cilindro 0–1,4 m.
- **Água invisível:** lago é `Area3D` e raycast ignorava áreas → `CollideWithAreas = true`; lago na altura do terreno; distância horizontal para beber.
- **Plantas esgotadas nunca voltavam** (logístico parado em B=0) → realocação após 60 s (plano 3.2).
- **Sono permanente:** `IsSleeping` nunca soltava (metade do metabolismo para sempre, andando e comendo) → acorda com ação de dormir < 0 ou dor; dormindo não anda/morde/bebe.
- **Nascer cheio (energia 1,0 > setpoint 0,8):** comer era punido no início da vida → política aprendia "ficar parado" → recém-nascido com energia 0,6 / água 0,7 (plano 8.2, gema).

### O que funcionou
- Currículo de abundância (`CREATURE_IA_PLANTS`, `CREATURE_IA_LAKES`): 2000 plantas/10 lagos → 1200/6 → padrão 700/3. Transferiu para o mundo padrão.
- 3 mundos paralelos num cérebro (`PolicySession`), blocos de ≤10 min em primeiro plano (evita o reaper de memória), avaliação sempre em seed 0 (nunca treinada).

### Lição de método
- Melhora entre blocos de treino com mapas diferentes era variação de mapa. Só a avaliação em mapa reservado contra baseline na mesma seed é evidência.

## Simulador próprio e desempenho (2026-11-27, madrugada)

### Mais mundos Godot não aceleravam o treino
- **Sintoma:** 3 → 6 mundos Godot: ~72 updates/min nos dois casos. GPU em 5–10 %, cada Godot a 100 % de um núcleo.
- **Causa:** o servidor Python atende cada mundo como sessão própria (um forward por mundo por tick e um update PPO por mundo a cada 64 ticks), tudo numa thread. O Godot gira a 100 % mesmo pausado esperando o cérebro (laço do motor), então o uso de CPU não mostra o gargalo.
- **Correção:** `CreatureIA.Sim` junta todos os mundos num STEP só (ids globais): um forward por tick para todas as criaturas.

### Update do v3 lento: GRUCell passo a passo
- **Sintoma:** update PPO do v3 ~1,8 s por rollout (98 % do tempo do cérebro num benchmark isolado); v2 0,27 s.
- **Causa:** `RecurrentActorCritic.sequence` fazia 64 chamadas de `GRUCell` (frente e trás) por minibatch.
- **Correção:** `nn.GRU` (cuDNN) por segmento entre reinícios de vida; mesma matemática (teste contra GRUCell de referência), checkpoints antigos remapeados no load. Update 1,8 s → 0,43 s.

### Overhead de Python por criatura no cérebro
- **Sintoma:** perfil com 288 criaturas: ~45 ms/tick fora da rede (recompensa 22 ms, decodificar STEP 12 ms, normalizar observação 11 ms).
- **Causa:** recompensa calculada criatura a criatura com validação em Python; STEP convertido em tupla de 288 000 floats; `RunningMeanStd` em numpy float64 na CPU.
- **Correção:** `HomeostaticReward.compute_totals` vetorizada (teste de igualdade com a escalar), `ObservationRows` (linhas numpy preguiçosas), estatística e normalização na GPU em float64. Cérebro 132 → 80 ms/tick.

### Dois `dotnet build` do mesmo projeto em paralelo
- **Sintoma:** `CreatureIA.Sim build failed` em um dos dois treinos lançados juntos.
- **Causa:** os dois builds escrevem o mesmo `obj/`; e um Sim rodando trava o exe de `bin/Release`.
- **Correção:** o script noturno compila uma vez e passa `-NoBuild`; cada run executa uma cópia do Sim em `benchmarks/runs/<run>/sim/` (também guarda a versão exata usada).

### Servidor asyncio não terminava depois do cliente sair (Python 3.12)
- **Sintoma:** servidor de sonda nunca escrevia o arquivo final.
- **Causa:** `async with server` chama `wait_closed()`, que no 3.12 espera todas as conexões fecharem; o handler não fechava o `writer`.
- **Correção:** `writer.close()` no `finally` do handler (o `LockstepServer` já fazia).

### Regressão do v3 era a curiosidade (T1.1)
- **Sintoma:** v3 (GRU) estacionava em ~550 s de vida enquanto o v2 (MLP) subia a ~740 s com o mesmo número de updates.
- **Diagnóstico:** 4 runs comparáveis no Sim (6 mundos × 24 000 ticks, seed 1, 374 updates cada): v2 511 → 740 s;
  v3 controle 530 → 560; **v3 sem curiosidade 534 → 680**; v3 com lr 1e-4 486 → 501 (pior).
- **Causa:** o bônus de novidade (RND, peso 0,02 sobre novidade normalizada) tem a mesma ordem de grandeza da recompensa
  homeostática já escalada; distrai a política de comer/beber. Lr menor não era o problema.
- **Correção:** v3 treina com `--curiosity 0` (`night-v3`). Curiosidade volta só com peso bem menor e medido (etapa 3).

### Paridade Sim × Godot: comparar curvas, não só médias
- **Sintoma:** vida média aleatória 349 s (Sim) × 376 s (Godot) num primeiro teste.
- **Verificação:** sonda que registra energia, água, temperatura, sono e vivas por tick: curvas iguais do nascimento à extinção (144 → 95 → 56 → 15 vivas). A diferença vinha do sorteio de spawn/plantas (RNG próprio do Sim, PCG32), não da física.
- **Lição:** validar ambiente novo por trajetória média por tick com a mesma política, não por um único número de resumo.
