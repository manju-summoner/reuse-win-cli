// RE-USE の Mamba の x_proj。depthwise の因果畳み込みと SiLU を、全結合層の入力の読み込み時に行う（説明は ReUseLinearCS）。
#define CAUSAL_CONV
#include "ReUseLinearCS.hlsl"
