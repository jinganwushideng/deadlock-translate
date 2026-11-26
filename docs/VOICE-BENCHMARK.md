# 队友语音识别实测

i5-13500H · RTX 4050 Laptop 6 GB · 驱动 617.42

每种模型与识别设备使用同一批 10 条 Windows 合成音频。下表取 7 条单人语音的预热后识别中位数，含语言检测，不含队友发言结束等待和联网翻译。
噪声为 0 dB 信噪比的合成宽带脉冲，另有纯噪声、静音和两路重叠音频。这批样本没有使用线上玩家语音，也没有测对局帧率。

| 模型 | CPU（秒） | CUDA 13（秒） | Vulkan（秒） | 观察 |
|---|---:|---:|---:|---|
| Base Q5_1 | 2.75 | 0.19 | 0.23 | 轻量。短英语报点出字快，中文噪声和游戏术语容易错。 |
| Small Q5_1 | 10.25 | 0.35 | 0.42 | 显卡均衡。中英短句表现稳定，纯噪声样本在 CUDA、Vulkan 出现误字。 |
| Medium Q5_0 | 28.30 | 0.84 | 0.83 | 体积较大。中英短句表现稳定，术语仍有误认，CPU 延迟高。 |
| Large-v3-Turbo Q5_0 | 整批超时 | 0.96 | 1.00 | 本轮中文噪声报点保留较完整，仍有术语误认，CPU 延迟高。 |

CUDA 13 测试使用 NVIDIA 官方 cuBLAS 13.0.2.14 和本机驱动。缺少运行库时，CUDA 选项会提示安装依赖。

完成测试的组合中，静音均未出字。Small GPU 的纯噪声样本有误字。多人同时发言时，模型有时只转写其中一路，有时跳过，游戏内使用统一的「队友语音」标记。

参考台词与合成场景

- `en-short` — Fall back.
- `en-callout` — Push the left lane. My ultimate is ready.
- `en-objective` — Take the soul urn and rotate to the mid boss.
- `zh-callout` — 左路推线，我的大招好了。
- `zh-objective` — 先拿魂瓮，然后去打中路首领。
- `en-bursts` — Push the left lane. My ultimate is ready.
- `zh-bursts` — 左路推线，我的大招好了。
- `silence` — 无语音
- `effects-only` — 无语音
- `two-speakers` — 中英两路报点同播

### Base Q5_1 · CPU（4 线程）

预热耗时 1.63 秒。

| 场景 | 时长（秒） | 识别（秒） | 输出 |
|---|---:|---:|---|
| effects-only | 4.00 | 2.71 | 无 |
| en-bursts | 4.00 | 2.73 | Push the left lane. My ultimate is ready. |
| en-callout | 4.00 | 2.77 | Push the left lane. My ultimate is ready. |
| en-objective | 2.83 | 2.79 | Take the sole and rotate to the mid-boss. |
| en-short | 1.12 | 2.67 | fall back. |
| silence | 3.00 | 2.64 | 无 |
| two-speakers | 3.88 | 2.88 | 无 |
| zh-bursts | 3.35 | 2.77 | 左路遵陷,我的大招好了 |
| zh-callout | 3.35 | 2.58 | 左路推線,我的大招好了。 |
| zh-objective | 3.69 | 2.75 | 先拿魂魂,然后去打中路手领。 |

### Base Q5_1 · CUDA 13 GPU

预热耗时 0.79 秒。

| 场景 | 时长（秒） | 识别（秒） | 输出 |
|---|---:|---:|---|
| effects-only | 4.00 | 0.13 | 无 |
| en-bursts | 4.00 | 0.19 | Push the left lane. My ultimate is ready. |
| en-callout | 4.00 | 0.16 | Push the left lane. My ultimate is ready. |
| en-objective | 2.83 | 0.16 | Take the sole and rotate to the mid-boss. |
| en-short | 1.12 | 0.13 | fall back. |
| silence | 3.00 | 0.13 | 无 |
| two-speakers | 3.88 | 0.41 | 无 |
| zh-bursts | 3.35 | 0.19 | 左路遵陷,我的大招好了 |
| zh-callout | 3.35 | 0.19 | 左路推線,我的大招好了。 |
| zh-objective | 3.69 | 0.20 | 先拿魂魂,然後去打中路手領。 |

### Base Q5_1 · Vulkan GPU

预热耗时 0.87 秒。

| 场景 | 时长（秒） | 识别（秒） | 输出 |
|---|---:|---:|---|
| effects-only | 4.00 | 0.21 | 无 |
| en-bursts | 4.00 | 0.23 | Push the left lane. My ultimate is ready. |
| en-callout | 4.00 | 0.20 | Push the left lane. My ultimate is ready. |
| en-objective | 2.83 | 0.23 | Take the sole and rotate to the mid-boss. |
| en-short | 1.12 | 0.20 | fall back. |
| silence | 3.00 | 0.19 | 无 |
| two-speakers | 3.88 | 1.68 | 无 |
| zh-bursts | 3.35 | 0.26 | 左路遵陷,我的大招好了 |
| zh-callout | 3.35 | 0.23 | 左路推線,我的大招好了。 |
| zh-objective | 3.69 | 0.24 | 先拿魂魂,然後去打中路手領。 |

### Small Q5_1 · CPU（4 线程）

预热耗时 5.07 秒。

| 场景 | 时长（秒） | 识别（秒） | 输出 |
|---|---:|---:|---|
| effects-only | 4.00 | 19.87 | 无 |
| en-bursts | 4.00 | 10.25 | Push the left lane. My ultimate is ready. |
| en-callout | 4.00 | 10.35 | Push the left lane. My ultimate is ready. |
| en-objective | 2.83 | 10.07 | Take the Solern and rotate to the mid-boss. |
| en-short | 1.12 | 9.97 | Fall back. |
| silence | 3.00 | 9.33 | 无 |
| two-speakers | 3.88 | 9.80 | Take the Solvrn and rotate to the mid-boss. |
| zh-bursts | 3.35 | 11.27 | 左路推線 我的大招好了 |
| zh-callout | 3.35 | 11.18 | 左路推線,我的大招好了。 |
| zh-objective | 3.69 | 9.64 | 先拿魂问,然后去打中路首领。 |

### Small Q5_1 · CUDA 13 GPU

预热耗时 1.29 秒。

| 场景 | 时长（秒） | 识别（秒） | 输出 |
|---|---:|---:|---|
| effects-only | 4.00 | 7.56 | , wasn't it? |
| en-bursts | 4.00 | 0.38 | Push the left lane. My ultimate is ready. |
| en-callout | 4.00 | 0.35 | Push the left lane. My ultimate is ready. |
| en-objective | 2.83 | 0.35 | Take the Solern and rotate to the mid-boss. |
| en-short | 1.12 | 0.27 | Fall back. |
| silence | 3.00 | 0.28 | 无 |
| two-speakers | 3.88 | 0.37 | Take the Solvrn and rotate to the mid-boss. |
| zh-bursts | 3.35 | 0.33 | 左路推線 我的大招好了 |
| zh-callout | 3.35 | 0.33 | 左路推線,我的大招好了。 |
| zh-objective | 3.69 | 0.38 | 先拿魂问,然后去打中路首领。 |

### Small Q5_1 · Vulkan GPU

预热耗时 1.02 秒。

| 场景 | 时长（秒） | 识别（秒） | 输出 |
|---|---:|---:|---|
| effects-only | 4.00 | 4.71 | 👏👏👏👏👏 👏👏👏👏👏👏 Good luck!! |
| en-bursts | 4.00 | 0.42 | Push the left lane. My ultimate is ready. |
| en-callout | 4.00 | 0.43 | Push the left lane. My ultimate is ready. |
| en-objective | 2.83 | 0.41 | Take the Solern and rotate to the mid-boss. |
| en-short | 1.12 | 0.35 | Fall back. |
| silence | 3.00 | 0.36 | 无 |
| two-speakers | 3.88 | 0.43 | Take the Solvrn and rotate to the mid-boss. |
| zh-bursts | 3.35 | 0.42 | 左路推線 我的大招好了 |
| zh-callout | 3.35 | 0.47 | 左路推線,我的大招好了。 |
| zh-objective | 3.69 | 0.44 | 先拿魂问,然后去打中路首领。 |

### Medium Q5_0 · CPU（4 线程）

预热耗时 16.13 秒。

| 场景 | 时长（秒） | 识别（秒） | 输出 |
|---|---:|---:|---|
| effects-only | 4.00 | 28.99 | 无 |
| en-bursts | 4.00 | 28.17 | Push the left lane. My ultimate is ready. |
| en-callout | 4.00 | 28.30 | Push the left lane. My ultimate is ready. |
| en-objective | 2.83 | 28.11 | Take the solarn and rotate to the mid boss. |
| en-short | 1.12 | 29.29 | Fall back. |
| silence | 3.00 | 33.38 | 无 |
| two-speakers | 3.88 | 29.62 | Take the sovereign and rotate to the mid-boss. |
| zh-bursts | 3.35 | 28.94 | 左路推線 我的大招好了 |
| zh-callout | 3.35 | 29.11 | 左路推线,我的大招好了 |
| zh-objective | 3.69 | 28.05 | 先拿魂問,然後去打中路首領 |

### Medium Q5_0 · CUDA 13 GPU

预热耗时 2.67 秒。

| 场景 | 时长（秒） | 识别（秒） | 输出 |
|---|---:|---:|---|
| effects-only | 4.00 | 0.68 | 无 |
| en-bursts | 4.00 | 0.82 | Push the left lane. My ultimate is ready. |
| en-callout | 4.00 | 0.84 | Push the left lane. My ultimate is ready. |
| en-objective | 2.83 | 0.81 | Take the solarn and rotate to the mid boss. |
| en-short | 1.12 | 0.66 | Fall back. |
| silence | 3.00 | 0.68 | 无 |
| two-speakers | 3.88 | 0.84 | Take the sovereign and rotate to the mid-boss. |
| zh-bursts | 3.35 | 0.86 | 左路推線,我的大招好了 |
| zh-callout | 3.35 | 0.84 | 左路推线,我的大招好了 |
| zh-objective | 3.69 | 0.90 | 先拿魂問,然後去打中路首領 |

### Medium Q5_0 · Vulkan GPU

预热耗时 10.87 秒。

| 场景 | 时长（秒） | 识别（秒） | 输出 |
|---|---:|---:|---|
| effects-only | 4.00 | 0.70 | 无 |
| en-bursts | 4.00 | 0.77 | Push the left lane. My ultimate is ready. |
| en-callout | 4.00 | 0.81 | Push the left lane. My ultimate is ready. |
| en-objective | 2.83 | 0.89 | Take the solarn and rotate to the mid boss. |
| en-short | 1.12 | 0.78 | Fall back. |
| silence | 3.00 | 0.76 | 无 |
| two-speakers | 3.88 | 0.86 | Take the sovereign and rotate to the mid-boss. |
| zh-bursts | 3.35 | 1.04 | 左路推線 我的大招好了 |
| zh-callout | 3.35 | 0.83 | 左路推线,我的大招好了 |
| zh-objective | 3.69 | 0.86 | 先拿魂問,然後去打中路首領 |

### Large-v3-Turbo Q5_0 · CPU（4 线程）

整批测试超过 420 秒上限，未取得完整中位数。

### Large-v3-Turbo Q5_0 · CUDA 13 GPU

预热耗时 2.79 秒。

| 场景 | 时长（秒） | 识别（秒） | 输出 |
|---|---:|---:|---|
| effects-only | 4.00 | 1.28 | 无 |
| en-bursts | 4.00 | 0.99 | Push the left lane. My ultimate is ready. |
| en-callout | 4.00 | 0.95 | Push the left lane. My ultimate is ready. |
| en-objective | 2.83 | 0.96 | Take the sole urn and rotate to the mid boss. |
| en-short | 1.12 | 0.97 | Fall back. |
| silence | 3.00 | 0.95 | 无 |
| two-speakers | 3.88 | 0.94 | Take the sole iron and rotate to the mid-boss. |
| zh-bursts | 3.35 | 0.97 | 左路推线,我的大招好了 |
| zh-callout | 3.35 | 0.90 | 左路推线 我的大招好了 |
| zh-objective | 3.69 | 0.95 | 先拿魂蚊 然后去打中路首领 |

### Large-v3-Turbo Q5_0 · Vulkan GPU

预热耗时 2.45 秒。

| 场景 | 时长（秒） | 识别（秒） | 输出 |
|---|---:|---:|---|
| effects-only | 4.00 | 2.31 | 无 |
| en-bursts | 4.00 | 1.00 | Push the left lane. My ultimate is ready. |
| en-callout | 4.00 | 1.18 | Push the left lane. My ultimate is ready. |
| en-objective | 2.83 | 1.16 | Take the sole urn and rotate to the mid boss. |
| en-short | 1.12 | 0.99 | Fall back. |
| silence | 3.00 | 1.06 | 无 |
| two-speakers | 3.88 | 1.05 | Take the sole iron and rotate to the mid-boss. |
| zh-bursts | 3.35 | 1.12 | 左路推线,我的大招好了 |
| zh-callout | 3.35 | 1.00 | 左路推线 我的大招好了 |
| zh-objective | 3.69 | 0.96 | 先拿魂蚊 然后去打中路首领 |
