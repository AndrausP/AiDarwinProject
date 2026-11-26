# AiDarwinProject — Criatura IA

Criaturas que **evoluem como na teoria de Darwin** num mundo todo em blocos (voxel). Cada animal tem um
cérebro que aprende a sobreviver (comer, beber, dormir, fugir) sem nenhum comportamento programado; a
população herda genes, sofre mutação e seleção natural, e corpos e comportamentos novos surgem com o tempo.

- **Mundo e corpo:** Godot 4.7 (.NET) + `CreatureIA.Core` em C# puro (voxel, física por blocos, fisiologia, medo).
- **Cérebro:** Python + PyTorch, PPO online. v3 atual: ~4 M parâmetros com memória (GRU) e curiosidade (RND); meta ~30 M.
- **Ponte:** protocolo binário em lockstep (`contracts/protocol-v2.json`).

Regras e objetivo: [`CLAUDE.md`](CLAUDE.md) · Roteiro com dependências: [`docs/ROADMAP.md`](docs/ROADMAP.md) ·
Plano detalhado: [`docs/tasks/002-voxel-evolucao.md`](docs/tasks/002-voxel-evolucao.md).

## Rodar
Requer na máquina: Godot 4.7.2 .NET portátil e SDK .NET 8 em `.tools/`, Python 3.12 com `uv` em `.venv/`
(veja `docs/tasks/001-fases-1-2.md`).

```powershell
.\scripts\watch.ps1 -Showcase      # sala branca com todos os exemplos
.\scripts\watch.ps1 -Fresh         # mundo normal com o cérebro treinado
.\scripts\train-ppo.ps1 -RunName v3-gru-a -Brain gru -Ticks 72000 -Seed 1 -Worlds 3
```

Testes: `dotnet test godot\tests\CreatureIA.Tests` e `cd brain; python -m pytest -q`.
