# Task 005 — Etapa 3: cultura, comportamento complexo, cérebro v4

**Depende de:** T2.9 · **Fecha com:** T3.8.

| ID | Task | Depende de | Entrega | Aceite |
|---|---|---|---|---|
| T3.1 | **Hábitos + imitação:** camada pessoal que nasce zerada; na infância o filhote imita adultos da família que estão indo bem; hábitos nunca passam por gene | T2.6a, T2.4a | `learning/habits.py` | sem imitação × com imitação: filhotes com imitação sobrevivem mais |
| T3.2 | **Eficácia de cavar e escalar por gene** (garras, força, tamanho) | T2.2b | Core | testes de eficácia |
| T3.3 | **Território:** ação de marcar cheiro no chão (decai), sentido de marca de estranho e de parente | T2.3 | Core + protocolo v4 (se mudar layout) | teste de decaimento |
| T3.4 | **Colocar bloco:** carrega 1 bloco cavado, coloca com custo; vigia de exploits (muralhas) | T2.0a | ação + testes | métrica de blocos colocados por função (ninho, muro) |
| T3.5 | **Cérebro v4 em degraus** (cada degrau só entra com ganho na avaliação congelada): (a) codificador por sentido · (b) atenção social · (c) GRU 2×1024 · (d) mapa mental espacial; cada degrau começa por destilação do anterior | T2.6b | `learning/brain_v4.py` | ~30 M no final; cada degrau ≥ o anterior com IC |
| T3.6 | **Modo realidade dura** (sem resgate) | T2.4b | flag | run de 10 anos simulados registrado |
| T3.7 | **Métricas de emergência com definição operacional:** bando (vizinho mais próximo × modelo nulo); migração (deslocamento médio correlacionado com a estação); toca (tempo sob bloco cavado); hábito noturno (atividade × luz); especiação (agrupamento + isolamento reprodutivo); nicho (divergência de dieta e hábitat); cultura (similaridade de comportamento entre parentes × não parentes) | T3.1–T3.6 | `benchmarks/emergence.py` + painel | cada métrica com teste em dados sintéticos |
| T3.8 | **Avaliação da etapa 3** | T3.7 | relatório | ≥ 2 comportamentos emergentes acima do modelo nulo. **Parar e mostrar.** |
