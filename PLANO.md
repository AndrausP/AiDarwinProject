# Criatura IA — Plano Completo

> Criatura em mundo 3D vivo (Godot) que **aprende durante a vida** e **evolui entre gerações** (efeito Baldwin).
> Objetivos básicos: aprender, adaptar, conservar energia, conservar o corpo, comer, beber, termorregular, dormir, reproduzir.
> Nada de comportamento de grupo/espreita programado — deve **emergir** da pressão do mundo.

---

## 0. Decisões fixadas

| Item | Decisão | Motivo |
|---|---|---|
| Engine do mundo | **Godot 4.3+ .NET (C#)** em 3D | Física 3D, headless pra treino, C# já é stack conhecida |
| Cérebro / treino | **Python + PyTorch** (servidor separado) | JAX sem suporte CUDA nativo no Windows; PyTorch tem `torch.func` p/ batch de cérebros |
| Comunicação | TCP localhost, binário, **lockstep** | Simples, determinístico, debugável |
| Algoritmo de aprendizado | **DreamerV3** (world model + imaginação) | Eficiente em amostras, hiperparâmetros robustos |
| Herança | **Baldwin** — filho herda só genoma, pesos nascem do zero | Evolução seleciona quem *aprende* bem |
| GPU | **6 GB VRAM** | Define tamanho do cérebro e população |
| Params por criatura | **~4M** (teto do projeto: 500M — respeitado com folga) | Cabe população de 48–64 em 6 GB |
| RAM sistema | ≥ 16 GB recomendado | Replay buffers e pesos ficam na RAM |

---

## 1. Visão geral da arquitetura

```
┌──────────────────────── GODOT (C#, headless no treino) ────────────────────────┐
│  World: terreno, água, plantas, temperatura, dia/noite, estações, perigos        │
│  Creature × N: corpo físico, sentidos, metabolismo, dano, reflexos               │
│  BrainBridge: a cada 6 ticks de física (10 Hz) envia obs, recebe ações            │
└───────────────────────────────▲──────────────┬──────────────────────────────────┘
                                │ ações         │ obs + eventos (nasc/morte/acasalamento)
┌───────────────────────────────┴──────────────▼──────────────────────────────────┐
│                         PYTHON (PyTorch, GPU)                                    │
│  Bridge server ─► Acting (batch de todos os cérebros, fp16)                      │
│  Replay buffer por indivíduo (RAM)                                               │
│  Trainer: DreamerV3 por indivíduo (round-robin na GPU)                           │
│  Evolution: genomas, crossover, mutação, arquivo, anti-extinção                  │
│  Metrics: TensorBoard + logs de genealogia                                       │
└──────────────────────────────────────────────────────────────────────────────────┘
```

**Regra de ownership:**
- Godot é dono da **física e do corpo** (posição, energia, dano, morte).
- Python é dono do **cérebro e do genoma** (pesos, mutação, recompensa).
- Godot nunca decide comportamento; Python nunca mexe em física.

---

## 2. Estrutura de pastas

```
criatura-ia/
├── PLANO.md
├── godot/                         # projeto Godot 4 .NET
│   ├── project.godot
│   ├── scenes/
│   │   ├── World.tscn
│   │   ├── Creature.tscn
│   │   └── Spectator.tscn         # câmera livre + overlays (modo assistir)
│   ├── scripts/
│   │   ├── World/
│   │   │   ├── TerrainGenerator.cs
│   │   │   ├── BiomeMap.cs        # temperatura, umidade, vegetação
│   │   │   ├── PlantManager.cs    # spawn/regrow de plantas (pool + MultiMesh)
│   │   │   ├── WaterBodies.cs
│   │   │   ├── DayNightCycle.cs
│   │   │   ├── SeasonCycle.cs
│   │   │   └── Hazards.cs         # espinhos, penhascos, zonas frias/quentes
│   │   ├── Creature/
│   │   │   ├── CreatureBody.cs    # CharacterBody3D + hitboxes por zona
│   │   │   ├── Senses.cs          # raycasts, audição, olfato, propriocepção
│   │   │   ├── Actuators.cs       # aplica ações no corpo
│   │   │   ├── Reflexes.cs        # reflexos fixos do genoma
│   │   │   └── CreatureState.cs   # ponte entre Godot e Core
│   │   ├── Core/                  # C# PURO (sem Godot) → testável com NUnit
│   │   │   ├── Metabolism.cs
│   │   │   ├── DamageModel.cs
│   │   │   ├── Thermoregulation.cs
│   │   │   ├── Reproduction.cs
│   │   │   └── BodyTraits.cs      # traços do corpo vindos do genoma
│   │   └── Net/
│   │       ├── BrainBridge.cs     # cliente TCP lockstep
│   │       └── Protocol.cs
│   └── tests/                     # NUnit sobre scripts/Core
├── brain/                         # Python
│   ├── pyproject.toml
│   ├── bridge/
│   │   ├── server.py
│   │   └── protocol.py
│   ├── models/
│   │   ├── encoder.py
│   │   ├── rssm.py
│   │   ├── heads.py               # decoder, reward, continue, actor, critic
│   │   ├── curiosity.py           # ensemble de disagreement
│   │   ├── plastic.py             # camada Hebbian (Fase 5)
│   │   └── instinct.py            # rede de instinto do genoma (Fase 4b)
│   ├── learning/
│   │   ├── dreamer.py             # loss + update de 1 indivíduo
│   │   ├── replay.py
│   │   └── reward.py              # recompensa homeostática
│   ├── evolution/
│   │   ├── genome.py
│   │   ├── operators.py           # mutação, crossover, distância genética
│   │   ├── population.py
│   │   └── archive.py             # hall of fame + anti-extinção
│   ├── runtime/
│   │   ├── acting.py              # inferência batched
│   │   ├── scheduler.py           # quem treina quando
│   │   └── checkpoint.py
│   ├── metrics/
│   │   ├── logger.py
│   │   └── emergence.py           # métricas de grupo, espreita, sinal
│   └── tests/                     # pytest
└── docs/
    └── decisions.md
```

---

## 3. O mundo (Godot 3D)

### 3.1 Terreno e biomas
- Mapa **256 × 256 m**, `HeightMapShape3D` gerado por ruído (FastNoiseLite) com seed.
- `BiomeMap`: 3 camadas de ruído → **temperatura**, **umidade**, **densidade de vegetação**.
- Biomas resultantes: campo aberto, floresta (oclusão de visão), região fria, região quente/seca, margem de lago.
- 2–4 **lagos** (Area3D de água). Água rasa = beber; água funda = nadar devagar, gasta mais energia.

### 3.2 Recursos
- **Plantas**: pool de ~400 instâncias (StaticBody3D + Area3D, render com MultiMesh).
  - Cada planta tem `biomassa` (0–1). Comer reduz. Cresce de volta: `dB/dt = r · B · (1 − B)` (logístico).
  - Taxa de crescimento `r` depende de umidade, estação e luz (dia).
  - Planta com B ≈ 0 some; re-spawn em ponto aleatório do bioma fértil.
- **Carcaças**: criatura morta vira carne (energia proporcional ao tamanho), apodrece em N minutos.
- **Plantas espinhosas** (~10%): dão mais energia, mas causam dano ao comer.

### 3.3 Ciclos
- **Dia/noite**: 1 dia = 10 min de sim. À noite, alcance dos raios ×0.4 e temperatura −8 °C.
- **Estações**: 1 ano = 8 dias de sim. Inverno: crescimento de plantas ×0.2, temperatura −10 °C. Verão: lagos rasos secam parcialmente.
- **Eventos de mudança** (Fase 5): planta nova aparece, bioma desloca, lago some — testa adaptação.

### 3.4 Perigos
- Queda: dano se velocidade vertical de impacto > limiar.
- Espinhos (arbustos): dano por contato.
- Frio/calor extremos: dano se temperatura corporal sair da faixa vital.
- Predação: **não programada** — surge se o gene `dieta` derivar pra carnívoro (ver 5).

### 3.5 Performance
- Treino: `godot --headless`, sem render. `Engine.TimeScale` ajustado ao que a GPU aguenta (alvo 4×).
- Física 60 Hz, cérebro **10 Hz** (decide a cada 6 ticks; ação repetida nos ticks intermediários).
- Raycasts em C# via `PhysicsDirectSpaceState3D.IntersectRay` com layers de colisão dedicadas (não usar milhares de nós `RayCast3D`).
- Modo assistir: instância com render + `Spectator.tscn`, população menor, mesmo servidor Python.

---

## 4. O corpo

### 4.1 Forma (v1 → v2)
- **v1 (Fases 1–6):** corpo cinemático `CharacterBody3D` — cápsula de torso + esfera de cabeça + zona de pernas. Locomoção por ações de alto nível (empuxo, giro). **Motivo:** aprender a andar com juntas desde o zero dobra o custo de treino; o foco é comportamento.
- **v2 (futuro):** corpo articulado (`RigidBody3D` + `Generic6DOFJoint3D`) com ações de torque por junta.

### 4.2 Zonas de dano
| Zona | Morte se integridade ≤ 0 | Efeito do dano |
|---|---|---|
| Cabeça | sim | reduz alcance dos sentidos |
| Torso | sim | reduz energia máxima efetiva |
| Pernas | não | reduz velocidade máxima |

### 4.3 Ações (vetor contínuo, 10 dims, `tanh` → [−1, 1])
| # | Ação | Uso |
|---|---|---|
| 0 | empuxo frente/trás | locomoção |
| 1 | giro | locomoção |
| 2 | marcha (devagar ↔ correr) | custo energético ∝ marcha² |
| 3 | inclinação da cabeça | mira os sentidos |
| 4 | pular | > 0.5 dispara |
| 5 | morder/comer | > 0.5 — come planta/carne ou fere criatura à frente |
| 6 | beber | > 0.5 perto de água |
| 7 | dormir | > 0.5 por 2 s entra no sono; acorda por dano, som alto ou ação < 0 |
| 8 | acasalar | > 0.5 sinaliza intenção |
| 9 | emitir som (intensidade) | canal livre, **sem significado definido** |

(+1 opcional: tom do som, na Fase 6)

### 4.4 Sentidos (vetor de observação ≈ 700 floats)
| Sentido | Formato | Dims |
|---|---|---|
| Visão | 24 raios horizontais × 3 alturas = 72 raios; cada um: distância normalizada + tipo one-hot (nada, terreno, água, planta, planta-espinho, carne, criatura, perigo) | 72 × 9 = 648 |
| Cor do alvo | RGB da criatura atingida (0 se não for criatura) — só raios do nível médio | 24 × 3 = 72 → **reduzido**: média por setor (4 setores × 3) = 12 |
| Audição | 4 quadrantes: intensidade somada + tom médio | 8 |
| Olfato | gradiente (nariz − corpo) de 3 cheiros: planta, água, carne/criatura | 9 |
| Propriocepção | velocidade local (3), velocidade angular, no chão, ângulo da cabeça, dormindo | 7 |
| Interocepção | energia, água, temp. corporal, integridade ×3 zonas, fadiga, dor, libido, idade normalizada, maturo | 11 |
| Tempo | sin/cos da hora do dia | 2 |
| **Total** | | **≈ 697** |

Alcance e campo de visão vêm do genoma. À noite o alcance cai.

---

## 5. Metabolismo e fisiologia (C# puro em `Core/`, testado com NUnit)

Todas as variáveis são normalizadas em [0, 1], exceto a temperatura (°C).

```
massa       = tamanho³ · (1 + 0.5·armadura)

energia    -= dt · ( basal · massa^0.75
                   + k_mov · massa · (empuxo² · marcha²)
                   + k_giro · giro²
                   + k_cerebro                       # cérebro também custa
                   + custo_termo + custo_cura )
             · (0.5 se dormindo)

agua       -= dt · ( a_basal + k_suor · max(0, T_corpo − T_set) )

T_corpo    += dt · (T_ambiente − T_corpo) · (1 − isolamento) · k_troca
              + aquecimento_por_tremor (gasta energia se T_corpo < T_set − 2)

fadiga     += dt · ( f_base + k_esforco · marcha² )      acordado
fadiga     -= dt · f_recuperacao                          dormindo
              fadiga > 0.8 → velocidade máx ×0.5, sentidos ×0.7

integridade_zona += dt · taxa_cura   se energia > 0.3 (custo_cura > 0)
integridade      -= dano · (1 − 0.6·armadura)

dor        = soma dos danos recentes (decai exponencialmente, τ = 3 s)
libido     cresce devagar após maturidade se energia > limiar_fertil; zera após acasalar

energia ≤ 0  → integridade do torso cai (fome)
agua ≤ 0     → integridade do torso cai (sede)
idade > idade_max → senescência: dano crescente
```

Digestão: `ganho_energia = biomassa · eficiencia(dieta)`, com
- planta: `eficiencia = 1 − dieta`
- carne: `eficiencia = dieta`

**Reflexos (fixos, sem aprendizado):** dano por contato → impulso automático pra trás com força `reflexo_forca` (gene). Sono interrompido por dor.

### Testes NUnit (exemplos)
- `Should_DecreaseEnergyFaster_When_CreatureIsRunning`
- `Should_HalveBasalCost_When_CreatureIsSleeping`
- `Should_DamageTorso_When_EnergyIsZero`
- `Should_NotHeal_When_EnergyBelowThreshold`
- `Should_KillCreature_When_HeadIntegrityReachesZero`

---

## 6. O cérebro (~4M params por criatura)

### 6.1 Blocos e orçamento
| Bloco | Detalhe | Params |
|---|---|---|
| Encoder | MLP 697 → 512 → 256, LayerNorm + SiLU, entrada em symlog | ~0.49M |
| RSSM | GRU 384; latente estocástico 16 categorias × 16 classes; prior e posterior MLP 384 | ~1.6M |
| Decoder | features (640) → 512 → 697 (reconstrói a observação) | ~0.69M |
| Reward head | 640 → 256 → 255 bins (twohot) | ~0.23M |
| Continue head | 640 → 256 → 1 | ~0.17M |
| Actor | 640 → 256 → 256 → 10 (média + desvio) | ~0.23M |
| Critic | 640 → 256 → 256 → 255 bins (+ cópia EMA alvo, sem custo de gradiente) | ~0.30M |
| Curiosidade | ensemble de 5 MLPs pequenos que preveem o próximo latente | ~0.30M |
| **Total treinável** | | **≈ 4.0M** |

Fase 4b adiciona a rede de instinto (~3k, evoluída, não treinada).
Fase 5 adiciona a camada plástica (~65k coeficientes).

### 6.2 DreamerV3 — config base
```yaml
seq_len: 64
batch_size: 16
imag_horizon: 15
discount: 0.997          # vida longa
lambda: 0.95
kl_dyn: 0.5
kl_rep: 0.1
free_nats: 1.0
unimix: 0.01
symlog_inputs: true
twohot_bins: 255
return_norm: percentile 5–95, decay 0.99
actor_entropy: 3e-4      # gene ajusta
lr_world_model: 1e-4     # gene ajusta
lr_actor: 3e-5           # gene ajusta
lr_critic: 3e-5          # gene ajusta
train_ratio: 32          # passos replayados por passo de ambiente
precision: fp32 no treino, fp16 na inferência (GPUs de 6 GB costumam não ter bf16)
```

### 6.3 Recompensa homeostática (`learning/reward.py`)
Calculada no Python a partir da interocepção. Godot não conhece recompensa.

```
desvio_i(t) = |var_i(t) − setpoint_i|
r_t = Σ_i w_i · (desvio_i(t−1) − desvio_i(t))     # melhorou o estado → positivo
      − w_dor · dor_t
      + β(idade) · curiosidade_t
      − 1.0 · morreu_t

i ∈ {energia, agua, temperatura, integridade, fadiga, libido}
β(idade) = β0 · exp(−idade / τ_curiosidade)          # filhote curioso, adulto menos
curiosidade = variância entre as predições do ensemble (disagreement)
```
`w_i`, setpoints, `w_dor`, `β0` e `τ_curiosidade` vêm do **genoma**: cada linhagem "sente" o mundo diferente e a evolução escolhe.

### 6.4 Sono = consolidação
- Criatura dormindo recebe **2× mais passos de treino** no scheduler.
- O replay durante o sono prioriza transições com alto erro de predição.
- Analogia: o sono consolida memória e gasta pouca energia.

### 6.5 Aprendizado contínuo (anti-esquecimento)
- Replay por indivíduo: capacidade de 50k passos, **50% FIFO recente + 50% reservoir** (amostra uniforme da vida inteira).
- Raios quantizados em `uint8` e resto em `float16` → ~0,8 KB/passo → ~40 MB por criatura.

---

## 7. Orçamento de VRAM (6 GB) e computação

### 7.1 Estratégia
- **Pesos e estado do otimizador de todos ficam na RAM do sistema** (pinned memory).
- **Inferência:** cópia fp16 de todos os actors + RSSMs empilhada na GPU, rodada em batch com `torch.func.functional_call` + `vmap`. Sincroniza a cada 50 passos de treino do indivíduo.
- **Treino:** round-robin — carrega um indivíduo (ou um grupo de 4 via `vmap` na v2) na GPU, faz K updates e devolve pra RAM.

### 7.2 Estimativa
| Item | VRAM |
|---|---|
| Contexto CUDA + overhead do alocador | ~0.8 GB |
| Inferência: 64 cérebros × ~2.5M params (actor + RSSM + encoder) × 2 bytes | ~0.3 GB |
| Treino de 1 indivíduo: pesos + grads + Adam (~64 MB) + ativações batch 16 × 64 + imaginação | ~0.5 GB |
| Treino v2 (grupo de 4 em vmap) | ~2.0 GB |
| **Total (v2)** | **~3.1 GB** → folga de ~2.9 GB |

RAM: 64 criaturas × (64 MB pesos/otimizador + 40 MB replay) ≈ **6.7 GB**.

### 7.3 Vazão esperada (estimativa — medir na Fase 3)
- 1 update DreamerV3 nesse tamanho ≈ 15–25 ms numa GPU de 6 GB → ~50 updates/s.
- train_ratio 32 e batch 16 × 64 = 1024 passos/update → sustenta ~1.600 passos de ambiente/s.
- 48 criaturas a 10 Hz = 480 passos/s em tempo real → **TimeScale ≈ 3×**.
- Vida de 50k decisões ≈ 1,4 h de sim ≈ ~30 min de parede. 50 gerações ≈ 1–2 dias de execução contínua.

Se ficar lento: baixa o `train_ratio` pra 16, reduz a população pra 32, ou corta o decoder pra 256.

### 7.4 Modo Lab (opcional)
Uma criatura só com cérebro de **~30–50M** (GRU 1024, MLP 1024) cabe em 6 GB. Serve pra estudar comportamento individual sem evolução.

---

## 8. Genoma e evolução (Baldwin)

### 8.1 Genes
| Grupo | Gene | Faixa | Mutação σ |
|---|---|---|---|
| Corpo | tamanho | 0.5–2.0 | 0.05 |
| | velocidade_max | 2–10 m/s | 0.3 |
| | alcance_visao | 10–40 m | 1.5 |
| | fov_visao | 90°–300° | 10° |
| | alcance_audicao | 5–40 m | 1.5 |
| | sensib_olfato | 0–1 | 0.05 |
| | isolamento | 0–1 | 0.05 |
| | armadura | 0–1 | 0.05 |
| | cor RGB | 0–1 ×3 | 0.05 |
| | dieta | 0 (herbívoro) – 1 (carnívoro) | 0.03 |
| Metabolismo | mult_basal | 0.5–1.5 | 0.05 |
| | taxa_cura | 0–1 | 0.05 |
| | idade_max | 20–80 min de sim | 2 |
| | idade_maturidade | 10–60% da idade_max | 3% |
| | limiar_fertil (energia) | 0.4–0.95 | 0.03 |
| | fracao_gema (energia dada ao filho) | 0.1–0.6 | 0.03 |
| | ninhada | 1–3 | ±1 com p = 0.05 |
| Drives | w_energia, w_agua, w_temp, w_integridade, w_fadiga, w_libido | 0–2 | 0.1 |
| | setpoints (energia, agua, temp) | por variável | 0.03 |
| | w_dor | 0–3 | 0.1 |
| | β0_curiosidade | 0–1 | 0.05 |
| | τ_curiosidade | 1–30 min | 1 |
| Aprendizado | lr_wm, lr_actor, lr_critic | 1e-5 – 1e-3 (espaço log) | 0.2 em log10 |
| | entropia_actor | 1e-4 – 1e-2 (log) | 0.2 em log10 |
| Reflexo | reflexo_forca | 0–1 | 0.05 |
| Meta | taxa_mutacao (auto-adaptativa) | 0.5–2.0 × σ | 0.05 |
| Instinto (Fase 4b) | pesos da rede de instinto (~3k) | — | 0.02 |

Os valores são clampados à faixa após mutar.

### 8.2 Reprodução
- **Fase 4a — assexuada:** maturo + energia > limiar_fertil + libido > 0.7 + ação acasalar → filho = genoma mutado.
- **Fase 4b — sexuada:** duas criaturas maturas, < 2 m de distância, ambas com acasalar > 0.5 dentro de 1 s, e **distância genética < limiar** (especiação emerge sozinha).
  - Filho: crossover uniforme + mutação.
  - Cada pai paga `fracao_gema · energia_max_filho + custo_fixo`.
  - Filho nasce ao lado com energia = gema.
- **Baldwin:** o cérebro do filho nasce com **pesos aleatórios**. Herda só o genoma. Replay vazio.

### 8.3 Seleção
- **Sem função de fitness explícita.** A seleção é natural: quem sobrevive e reproduz passa os genes.
- **Fitness de log** (não usada pra selecionar): nº de descendentes, tempo de vida, idade na primeira refeição.
- **Anti-extinção:** se a população cair abaixo de 16, nascem filhos mutados do **arquivo** (top 50 genomas por nº de descendentes, histórico).
- **Teto:** 64 vivas (limite de VRAM/RAM). Acima disso, a reprodução falha (o mundo sem recurso já limita antes).

### 8.4 Instinto evoluído (Fase 4b — compatível com Baldwin)
Rede pequena do genoma (obs reduzida 64 → 32 → 10, ~3k params). Não é aprendida, **evolui**:
```
ação = actor(s) + α(idade) · instinto(s)
α(idade) = exp(−idade / τ_instinto)       # τ_instinto também é gene
```
O filhote nasce com comportamentos inatos que somem conforme o cérebro aprende. Isso não é Lamarck: nenhum peso aprendido é herdado.

### 8.5 Sinal de Baldwin (o que medir)
- **Tempo até a primeira refeição/bebida na vida** cai ao longo das gerações → a evolução está selecionando melhores aprendizes.
- Os genes de aprendizado (lr, β, w_i) convergem.

---

## 9. Protocolo Godot ↔ Python

TCP localhost, porta 5555. Cada mensagem: `[u32 tamanho][u8 tipo][payload]`. Arrays em float32 little-endian.

| Tipo | Direção | Payload |
|---|---|---|
| `HELLO` | G → P | versão, obs_dim, act_dim, seed do mundo |
| `CONFIG` | P → G | parâmetros do mundo, TimeScale |
| `STEP` | G → P | tick, N, ids[N] u32, obs[N × 697], eventos[] |
| `ACT` | P → G | tick, N, ids[N], acoes[N × 10] |
| `SPAWN` | P → G | id, traços do corpo (genes de corpo/metabolismo), posição opcional |

Eventos dentro de `STEP`:
- `DEATH(id, causa)` — causa: fome, sede, frio, calor, queda, mordida, idade
- `MATE_REQUEST(id_a, id_b)` — Python cria o genoma e responde com `SPAWN`
- `ATE(id, tipo, quantidade)`, `DAMAGED(id, zona, fonte)` — só pra métricas

**Lockstep:** o Godot envia `STEP`, pausa a física até chegar `ACT` do mesmo tick, aplica e avança 6 ticks.
Alvo de latência de ida e volta: < 5 ms.

(Referência opcional: plugin `godot_rl_agents` tem sensores e sync prontos — avaliar na Fase 0, mas o cérebro e o loop são próprios.)

---

## 10. Fases de implementação

Tamanho: **P** pequeno · **M** médio · **G** grande.

### Fase 0 — Fundação (P)
- [ ] Projeto Godot 4 .NET + projeto Python (venv, PyTorch CUDA).
- [ ] `Protocol.cs` / `protocol.py` + testes de serialização dos dois lados.
- [ ] Bridge lockstep com 48 cápsulas e ações aleatórias.
- [ ] Rodar headless com `TimeScale`.

**Aceite:** 48 agentes aleatórios a 10 Hz, headless, ≥ 4× tempo real; ida e volta < 5 ms; 0 ticks perdidos em 10 min.

### Fase 1 — Mundo e corpo (G)
- [ ] Terreno, biomas, lagos, plantas com regrowth, dia/noite, estações.
- [ ] Corpo v1, zonas de dano, ações, sentidos completos.
- [ ] `Core/` — metabolismo, dano, termorregulação + testes NUnit.
- [ ] Eventos de morte com causa.
- [ ] Spectator com overlays: barras de estado, raios visíveis.

**Aceite:** agentes aleatórios morrem de fome/sede num tempo coerente com as fórmulas (±20%); testes NUnit passam; o histograma de causas de morte faz sentido.

### Fase 2 — Baseline de sanidade (P)
- [ ] PPO simples com **pesos compartilhados** (~0.5M) e recompensa homeostática.
- Objetivo: validar se obs e recompensa permitem aprender **antes** de investir no Dreamer.

**Aceite:** tempo médio de vida ≥ 5× o do agente aleatório; a criatura procura comida/água quando o nível cai.

### Fase 3 — Cérebro individual DreamerV3 (G)
- [ ] `encoder`, `rssm`, `heads`, `dreamer.py`, `replay.py`.
- [ ] Inferência batched (vmap) + scheduler round-robin + offload pra RAM.
- [ ] Curiosidade por ensemble + β decaindo.
- [ ] Sono com prioridade de treino.
- [ ] Checkpoint de população (genomas + pesos + estado do otimizador; replay opcional).
- [ ] Medir vazão real e ajustar `train_ratio` / população.

**Aceite:** um cérebro do zero aprende a sobreviver em ≤ 50k passos; eficiência amostral melhor que a Fase 2; dorme espontaneamente com fadiga alta; VRAM < 5 GB no pico.

### Fase 4a — Evolução assexuada (M)
- [ ] `genome.py`, `operators.py`, `population.py`, `archive.py`.
- [ ] Traços do genoma aplicados ao corpo no `SPAWN`.
- [ ] Reprodução assexuada, anti-extinção, teto.
- [ ] Log de genealogia (pai → filho, genes, causa da morte).

**Aceite:** ≥ 50 gerações sem extinção definitiva; distribuição de genes deriva; tempo até a primeira refeição cai (sinal de Baldwin).

### Fase 4b — Sexuada + instinto (M)
- [ ] Acasalamento sexuado, crossover, distância genética.
- [ ] Rede de instinto evoluída com α(idade).

**Aceite:** acasalamento acontece de forma consistente; filhotes com instinto sobrevivem mais nos primeiros minutos que sem instinto (A/B).

### Fase 5 — Adaptação (M)
- [ ] Camada plástica Hebbian modulada (`plastic.py`): `h = W·x + (α ⊙ Hebb)·x`, com `Hebb ← (1 − η)·Hebb + η·m·(x ⊗ h)` e `m` = neuromodulação de saída da rede. Hebb zera no nascimento.
- [ ] Eventos de mudança no mundo (comida troca de lugar, bioma desloca).
- **Fallback:** se a camada plástica não ajudar, a adaptação fica a cargo do Dreamer online + curiosidade.

**Aceite:** tempo de recuperação do desempenho após uma mudança no mundo menor com plasticidade que sem (A/B).

### Fase 6 — Ecossistema e emergência (G)
- [ ] Gene `dieta` ativo: carne, mordida em criatura viva, carcaça.
- [ ] Cor visível pros outros (camuflagem pode emergir).
- [ ] Canal de som com tom.
- [ ] Opcional: 2 mundos-ilha com migração rara (modelo de ilhas).
- [ ] `emergence.py` instrumentado (ver 11).

**Aceite:** métricas coletadas e plotadas. A emergência em si **não é garantida** — é o experimento.

### Fase 7 — Visualização e ferramentas (M)
- [ ] Spectator: seguir uma criatura, mostrar intenção do actor, valor do critic e "sonho" (decoder da imaginação).
- [ ] Árvore genealógica e gráficos de genes no tempo.
- [ ] Replay de uma vida inteira.

---

## 11. Métricas de emergência (`metrics/emergence.py`)

| Comportamento | Métrica |
|---|---|
| Agrupamento | distância média ao vizinho mais próximo vs baseline de posições aleatórias; tamanho médio de cluster (DBSCAN) |
| Movimento coordenado | alinhamento médio de direção dos vizinhos < 5 m |
| Espreita | eventos "parado/lento < 10 m de outra criatura por > 5 s seguido de mordida"; razão vs baseline |
| Fuga | aceleração para longe após ver um carnívoro |
| Comunicação | informação mútua entre som emitido e contexto (perigo/comida por perto); mudança de ação de quem ouve |
| Camuflagem | correlação entre cor e cor média do bioma onde vive |
| Especiação | clusters no espaço genético (k-means + silhouette) |
| Divisão de nicho | distribuição bimodal do gene `dieta` |

---

## 12. Riscos e mitigação

| Risco | Mitigação |
|---|---|
| Vida curta demais pra aprender do zero (Baldwin) | gema de energia generosa no início, filhote com metabolismo menor, instinto evoluído (4b), curiosidade alta no início |
| Treino lento em 6 GB | train_ratio 16, população 32, decoder menor, vmap em grupos |
| Esquecimento catastrófico | replay reservoir + consolidação no sono |
| Reward hacking via curiosidade (ruído "interessante") | disagreement em vez do erro bruto; β decai com a idade |
| Extinção em massa | arquivo + anti-extinção; ajustar a abundância de plantas |
| Explosão populacional | teto rígido + recursos logísticos |
| Gargalo dos raycasts no Godot | `IntersectRay` direto em C#, layers dedicadas, raios a 10 Hz (não 60) |
| Dessincronização no lockstep | id de tick em toda mensagem; assert dos dois lados |
| Instabilidade numérica | symlog, twohot, LayerNorm, gradient clip 100 (padrão DreamerV3) |

---

## 13. Ordem de leitura recomendada

1. Hafner et al. — *Mastering Diverse Domains through World Models* (**DreamerV3**)
2. Sekar et al. — *Planning to Explore via Self-Supervised World Models* (**Plan2Explore**, curiosidade por disagreement)
3. Keramati & Gutkin — *Homeostatic reinforcement learning* (recompensa homeostática)
4. Miconi et al. — *Differentiable plasticity* / *Backpropamine* (camada plástica)
5. Hinton & Nowlan — *How learning can guide evolution* (**efeito Baldwin**)

---

## 14. Próximo passo imediato

**Fase 0**: criar o esqueleto do projeto (Godot .NET + Python), o protocolo e a bridge lockstep com 48 agentes aleatórios.
