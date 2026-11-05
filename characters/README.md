# claudia-pet 角色素材

本项目当前随附 `claude/` 下的 Claude 娘素材：铜橙色头发、橙花发饰、Clawd 玩偶及相关动作、问候、节日立绘和预设台词。默认显示名为 `claudia`，用户可在设置中改名。

这些素材来自 [CrossPet](https://github.com/lokicorvus/crosspet) v1.3.8（lokicorvus），立绘和台词文件与原版一致，只把 `character.json` 里的默认名字从「Claude」改成了「claudia」，并删去了不再使用的贴边立绘。按 CrossPet 的说明，立绘由 CrossPet 用 AI 生图工具绘制，形象参考社区流行的 Claude 娘设定（如 [ai-school-op](https://github.com/lshhhhhhh/ai-school-op)、[openpet-ai-girls](https://github.com/AwesomeHou/openpet-ai-girls)），社区设定最初的作者未确认。素材沿用 CC BY-NC-SA 4.0：可以转载、改编，但须署名、不得商用，改编后按同一协议发布。立绘中的 Clawd 玩偶是 Anthropic 的形象，相关权利归 Anthropic。完整来源和授权见本目录的 [LICENSE.md](LICENSE.md) 及项目 [NOTICE.md](../NOTICE.md)。

`LICENSE.md` 是原始授权文档，保留了原项目全部角色的来源表；当前 claudia-pet 包含的角色以本目录实际文件为准。不要把原作者和授权记录当作无用品牌信息删除。

碰边动作立绘 `bonk.webp`、`stagger.webp`、`stagger-2.webp`、`fallen.webp`、`faceplant.webp`、`getup.webp` 不是 CrossPet 原有的：它们以 CrossPet 的设计参考图和现有立绘为参考，在 OpenAI Codex 中用内置 image_gen 生成，再用 `scripts/prepare-sprites.py` 统一大小、底边对齐。同样沿用 CC BY-NC-SA 4.0，提示词见 [docs/prompts/](../docs/prompts/)。`character.json` 里这几个动作的台词（`bonk`、`stagger`、`fallen`、`faceplant`、`getup`、带 `Angry` 后缀的生气版和 `tumbleAsk`）也是本项目新写的，其余台词与 CrossPet 原版一致。

听你说、看书、忙碌左右、查资料、哎呀、累了、鼓脸、赌气、晕乎乎、被哄、原谅你、六个时段问候、元旦这 19 张也不是 CrossPet 原图：2026-11-05 按动画的身体比例重画（裙摆下露一小截白袜和小皮鞋），姿势和道具照原图，提示词见 [docs/prompts/立绘重画-统一比例/](../docs/prompts/立绘重画-统一比例/)，同样沿用 CC BY-NC-SA 4.0。

`claude/anim/` 里是动画动作的精灵图，来自 hunmer 的 claude-pet 动作包（[hunmer/aboutme-pets](https://github.com/hunmer/aboutme-pets) 的 `pets/claude`），作者书面授权按 CC BY-NC-SA 4.0 使用，原样复制没有改动；每张用在哪个姿态见 [claude/anim/SOURCE.md](claude/anim/SOURCE.md)，播放配置在 `character.json` 的 `animations`。被动画替换的姿态（思考、写东西、摸头、节日等）原来的静态立绘已经从项目里移除，只留待机图 `idle.webp` 和眨眼帧：程序靠待机图识别角色，系统要求减少动画、动画读不到时也用它兜底；还有静态立绘的姿态（累了、碰边动作等）照常用静态图。自己加的角色不写 `animations` 就只用静态立绘。

`avatar.webp`、`portrait.webp`、`portrait-card.webp` 是展示素材，不作为普通动作播放。`clawd.webp` 是抱 Clawd 的现有立绘，也作为 [软件图标](../icon/README.md) 的制作参考。
