# Roteiro — fases, tasks e dependências

Objetivo: criaturas que evoluem como na teoria de Darwin (`CLAUDE.md`). Regra: **cada etapa fecha com
avaliação congelada na seed 0 contra o aleatório e para para mostrar ao usuário** antes da próxima.

| Etapa | Arquivo de tasks | Estado |
|---|---|---|
| 1 — Mundo voxel + cérebro base | `docs/tasks/003-etapa-1-fechamento.md` (T1.1–T1.8) | ⏳ fechamento |
| 2 — Simulador próprio, ilha, genes, reprodução, predadores | `docs/tasks/004-etapa-2.md` (D1–D4, T2.0–T2.9) | ⬜ |
| 3 — Cultura, comportamento complexo, cérebro v4 ~30 M | `docs/tasks/005-etapa-3.md` (T3.1–T3.8) | ⬜ |
| 4 — Ecologia avançada | `docs/tasks/006-etapa-4-e-transversais.md` (T4.1–T4.4) | ⬜ |
| Transversais | `docs/tasks/006-etapa-4-e-transversais.md` (TX.1–TX.6) | contínuo |

Registro das decisões de design: `docs/tasks/002-voxel-evolucao.md`.

```mermaid
flowchart TD
    T11[T1.1 regressão v3] --> T15[T1.5 treinos finais]
    T12[T1.2 avaliação congelada] --> T14[T1.4 avaliação 3 políticas]
    T13[T1.3 métrica de sono] --> T14
    T14 --> T18[T1.8 AVALIAÇÃO ETAPA 1]
    T15 --> T18
    T16[T1.6 HUD + inspetor] --> T18
    T17[T1.7 vigia de exploits] --> T18

    T18 --> D[Decisões D1-D4]
    T18 --> T20a[T2.0a sentidos/plantas/corpo no Core]
    T20a --> T20b[T2.0b CreatureIA.Sim] --> T20c[T2.0c snapshot do mundo] --> T20d[T2.0d Godot visualizador]
    T18 --> T20e[T2.0e fp16 + GRU fundido]
    T20a --> T21a[T2.1a ilha, rios, mar salgado] --> T21c[T2.1c estações por região]
    T21a --> T21b[T2.1b nadar cansa] 
    T21a --> T21d[T2.1d escala 200 criaturas]
    D --> T22a[T2.2a genética diploide] --> T22b[T2.2b corpo modular]
    T22a --> T22c[T2.2c ciclo de vida]
    T21c --> T22c
    T22a --> T22d[T2.2d acuidade dos sentidos]
    T22a --> T22e[T2.2e motivações como genes]
    T21c --> T23[T2.3 protocolo v3]
    T22b --> T23
    T22c --> T23
    T22d --> T23
    T22e --> T23
    T23 --> T24a[T2.4a reprodução] --> T24b[T2.4b população por nascimentos]
    T24b --> T25[T2.5 predadores]
    T23 --> T26a[T2.6a instinto]
    T20e --> T26b[T2.6b memória longa]
    T24a --> T27a[T2.7a genealogia SQLite] --> T27b[T2.7b Observatório]
    T20d --> T27b
    T24b --> T28[T2.8 controle sem seleção]
    T25 --> T29[T2.9 AVALIAÇÃO ETAPA 2]
    T26a --> T29
    T26b --> T29
    T27b --> T29
    T28 --> T29

    T29 --> T31[T3.1 hábitos + imitação]
    T29 --> T32[T3.2 cavar/escalar por gene]
    T29 --> T33[T3.3 território]
    T29 --> T34[T3.4 colocar bloco]
    T29 --> T35[T3.5 cérebro v4 em degraus]
    T29 --> T36[T3.6 realidade dura]
    T31 --> T37[T3.7 métricas de emergência]
    T32 --> T37
    T33 --> T37
    T34 --> T37
    T35 --> T37
    T36 --> T37
    T37 --> T38[T3.8 AVALIAÇÃO ETAPA 3] --> E4[T4.x ecologia avançada]
```

**Caminho crítico até a evolução darwiniana funcionando:** T1.8 → T2.0a → T2.0b → (D1–D4) → T2.2a →
T2.3 → T2.4a → T2.4b → T2.8 → T2.9.

## Revisão do plano (2026-11-26): lacunas encontradas e onde foram tratadas
| # | Lacuna | Gravidade | Tratada em |
|---|---|---|---|
| 1 | Critério da etapa 1 pede "dormir com fadiga alta", mas o benchmark não mede sono | alta | T1.3 |
| 2 | Não existe avaliação congelada: o cérebro continua aprendendo durante a avaliação (contamina o resultado) | alta | T1.2 |
| 3 | O benchmark compara só 2 políticas; a etapa 1 precisa de aleatório × v2 × v3 | média | T1.4 |
| 4 | Regressão do v3 (vida média caindo) sem diagnóstico | alta | T1.1 |
| 5 | **Recompensa só homeostática não motiva acasalar nem cuidar de filhote**: sem isso, reprodução e cuidado parental não aparecem | **crítica** | D3 + T2.2e |
| 6 | **O mundo não persiste:** só o cérebro tem checkpoint; evolução de anos precisa de snapshot do mundo e da população | **crítica** | T2.0c |
| 7 | Como assistir uma população que evolui dentro do simulador próprio (sem Godot) não estava definido | alta | D4 + T2.0d |
| 8 | Espécies iniciais contraditórias ("herbívoro + carnívoro" × "A 3 anos, B 20 anos") | média | D1 |
| 9 | Dono da genética contraditório (Python no plano antigo × simulador C#) | média | D2 |
| 10 | Teto de população (64 × 150–250) e escala do mapa (384 × 768) contraditórios | baixa | corrigido no 002 |
| 11 | Faltava controle científico: provar que a mudança de genes vem da seleção, e não de deriva | alta | T2.8 |
| 12 | Afogamento e mar aberto não especificados (fronteira natural da ilha) | média | T2.1b |
| 13 | Métricas de emergência sem definição operacional (como medir "migração", "bando") | alta | T3.7 |
| 14 | Checkpoints fora do git sem caminho até o desktop da RTX 2060 | média | TX.2 |
| 15 | Sem CI: os testes só rodam na mão | baixa | TX.1 |
| 16 | Sem orçamento de desempenho por etapa (risco de ficar lento sem perceber) | média | TX.4, T2.1d |
| 17 | Etapa 2 descrita duas vezes no 002 (original + ampliações) | baixa | unificada em 004 |
