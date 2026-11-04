# claudia-pet 角色素材

本项目当前随附 `claude/` 下的 Claude 娘素材：铜橙色头发、橙花发饰、Clawd 玩偶及相关动作、问候、节日立绘和预设台词。默认显示名为 `claudia`，用户可在设置中改名。

这些素材来自 [CrossPet](https://github.com/lokicorvus/crosspet) v1.3.8（lokicorvus），立绘和台词文件与原版一致，只把 `character.json` 里的默认名字从「Claude」改成了「claudia」，并删去了不再使用的贴边立绘。按 CrossPet 的说明，立绘由 CrossPet 用 AI 生图工具绘制，形象参考社区流行的 Claude 娘设定（如 [ai-school-op](https://github.com/lshhhhhhh/ai-school-op)、[openpet-ai-girls](https://github.com/AwesomeHou/openpet-ai-girls)），社区设定最初的作者未确认。素材沿用 CC BY-NC-SA 4.0：可以转载、改编，但须署名、不得商用，改编后按同一协议发布。立绘中的 Clawd 玩偶是 Anthropic 的形象，相关权利归 Anthropic。完整来源和授权见本目录的 [LICENSE.md](LICENSE.md) 及项目 [NOTICE.md](../NOTICE.md)。

`LICENSE.md` 是原始授权文档，保留了原项目全部角色的来源表；当前 claudia-pet 包含的角色以本目录实际文件为准。不要把原作者和授权记录当作无用品牌信息删除。

碰边动作立绘 `bonk.webp`、`stagger.webp`、`stagger-2.webp`、`fallen.webp`、`faceplant.webp`、`getup.webp` 不是 CrossPet 原有的：它们以 CrossPet 的设计参考图和现有立绘为参考，在 OpenAI Codex 中用内置 image_gen 生成，再用 `scripts/prepare-sprites.py` 统一大小、底边对齐。同样沿用 CC BY-NC-SA 4.0，提示词见 [docs/prompts/](../docs/prompts/)。`character.json` 里这几个动作的台词（`bonk`、`stagger`、`fallen`、`faceplant`、`getup`、带 `Angry` 后缀的生气版和 `tumbleAsk`）也是本项目新写的，其余台词与 CrossPet 原版一致。

`avatar.webp`、`portrait.webp`、`portrait-card.webp` 是展示素材，不作为普通动作播放。`clawd.webp` 是抱 Clawd 的现有立绘，也作为 [软件图标](../icon/README.md) 的制作参考。
