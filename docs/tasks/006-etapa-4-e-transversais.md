# Task 006 — Etapa 4 e tasks transversais

## Etapa 4 — Ecologia avançada (depende de T3.8)
| ID | Task | Depende de | Aceite |
|---|---|---|---|
| T4.1 | Doenças e parasitas: contágio por proximidade e por carcaça, genes de imunidade com custo | T3.8 | epidemia surge e a resistência aumenta por seleção (× controle) |
| T4.2 | Plantas que evoluem (defesa química/espinhos × herbívoros), polinização | T3.8 | coevolução medida |
| T4.3 | Guardar comida: ação carregar e esconder | T3.4 | estoques usados no inverno acima do acaso |
| T4.4 | Avaliação da etapa 4 | T4.1–T4.3 | parar e mostrar |

## Transversais (valem para todas as etapas)
| ID | Task | Quando | Aceite |
|---|---|---|---|
| TX.1 | **CI no GitHub Actions:** `dotnet test` + `pytest` a cada push (sem Godot e sem GPU) | já | badge verde no README |
| TX.2 | **Guarda dos checkpoints** fora do git (GitHub Releases por marco, ou pasta sincronizada) para levar ao desktop | antes do 1º treino no desktop | checkpoint baixado e retomado no desktop |
| TX.3 | **Vigia de exploits** em todo run longo (ver T1.7) | contínuo | relatório de avaliação sempre traz os contadores |
| TX.4 | **Budget de desempenho:** ms/tick e passos/s registrados em toda avaliação; regressão > 20% vira bug | contínuo | tabela na task da etapa |
| TX.5 | **Base de conhecimento:** cada erro e lição em `docs/knowledge/errors-aprendidos.md`; decisões em `docs/decisions.md` | contínuo | entradas datadas |
| TX.6 | **Commits por task** (Conventional Commits) e push no fim de cada task | contínuo | histórico legível |
