import torch
from torch import nn

from creature_brain.learning.ppo import LOG_STD_INIT, LOG_STD_MAX, LOG_STD_MIN, project_log_std


def test_log_std_starts_below_cap_so_gradient_flows():
    assert LOG_STD_MIN < LOG_STD_INIT < LOG_STD_MAX


def test_projection_pulls_parameter_back_inside_and_keeps_gradient():
    parameter = nn.Parameter(torch.tensor([-0.498, -6.0, -1.0]))
    project_log_std(parameter)
    assert parameter.detach().tolist() == [LOG_STD_MAX, LOG_STD_MIN, -1.0]
    parameter.clamp(LOG_STD_MIN, LOG_STD_MAX).sum().backward()
    assert torch.all(parameter.grad == 1.0)  # a parameter on the bound still learns
