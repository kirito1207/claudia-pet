# 来源与授权

claudia-pet 作为独立项目维护，基于 [CrossPet](https://github.com/lokicorvus/crosspet)（lokicorvus，来源版本 v1.3.8），同时参考 [dsh-pet](https://github.com/PC2005-cloud/dsh-pet)（PC2005-cloud，来源版本 0.3.7）。

## 代码

项目代码采用 MIT 协议。CrossPet 的版权与许可见 [LICENSE](LICENSE) 和 [原始许可](upstream/CrossPet-LICENSE)；来源版本记录见 `upstream/CrossPet-VERSION`。

`web/dsh-physics.js` 直接摘录 dsh-pet 的 `runtime/electron-helper/shared-core.js` 中独立的物理函数；网页物理适配器按其桌面驱动方式接入。右键「动作 → 分组 → 具体动作」菜单和白底圆角的柔和菜单样式参照 dsh-pet 的交互与样式，由本项目自行实现（悬停色改为浅橙）。dsh-pet 的版权和 MIT 许可全文保留在本文末尾。

## 角色素材与图标

本项目随附的 Claude 娘立绘、角色设定、台词和展示图（`characters/claude/`）除下文的碰边动作立绘和台词外都来自 CrossPet v1.3.8：原有立绘和台词与原版一致，只把默认显示名从「Claude」改为「claudia」，删去了不再使用的贴边立绘和已被下文动画动作替换的 22 张立绘（待机图保留），另有 20 张换成了下文按动画比例重画的版本，并在 `character.json` 里新增了碰边动作的台词（`bonk`、`stagger`、`fallen`、`faceplant`、`getup`、带 `Angry` 后缀的生气版和 `tumbleAsk`，由本项目新写）。按 CrossPet README「致谢与授权」一节的说明：

- 这些立绘由 CrossPet 用 AI 生图工具绘制；
- Claude 娘形象参考社区流行的 AI 娘设定，CrossPet 列出的参考为 [ai-school-op](https://github.com/lshhhhhhh/ai-school-op) 和 [openpet-ai-girls](https://github.com/AwesomeHou/openpet-ai-girls)；
- 社区 Claude 娘设定最初由谁创作，CrossPet 没有写明，本项目也未能确认。

这些素材按 CrossPet 的说明以 CC BY-NC-SA 4.0 授权，原文见 [characters/LICENSE.md](characters/LICENSE.md)。

软件图标（claudia 抱 Clawd 的单人透明头像）是本项目制作的衍生图：以 CrossPet 原四人图标中右下角的 Claude 娘头像、CrossPet 立绘 `characters/claude/clawd.webp` 为参考，在 OpenAI Codex 中用其内置的 image_gen 图像编辑工具生成，之后只做了 PNG / WebP / ICO 格式与尺寸转换。图标沿用 CC BY-NC-SA 4.0，使用的完整提示词保存在 [icon/README.md](icon/README.md)。

碰边动作立绘（撞头 `bonk`、`bonk-2`、`bonk-3`，摇晃 `stagger`、`stagger-2`、`stagger-3`，趴地 `faceplant`、`faceplant-2`，摔倒 `fallen-1`、`fallen`、`fallen-2`，连着翻页播放）也是本项目制作的衍生图。2026-11-06 按动画比例重画并补了过渡帧：以 CrossPet 的 Claude 娘设计参考图 `ref-claude.webp`、claude-pet 动作包的待机帧（比例参考）和 CrossPet 风格的旧碰边立绘或本套上一帧（姿势参考）为附图，在 OpenAI Codex 中用其内置的图片生成功能生成，再由 `scripts/prepare-sprites.py` 统一缩放、对齐并转成 WebP。提示词的「通用格式规则」和「角色锁定」两段大部分取自 CrossPet 的角色生图提示词。这些立绘沿用 CC BY-NC-SA 4.0，提示词和附图见 [docs/prompts/碰边补帧/](docs/prompts/碰边补帧/)（更早的版本见 [docs/prompts/](docs/prompts/)）。原来的「爬起」动作 2026-11-06 起不再使用。

按动画比例重画的 19 张立绘（`listening`、`reading`、`busy_left`、`busy_right`、`searching`、`oops`、`tired`、`puffed`、`sulk`、`dizzy`、`coaxed`、`forgiven`、六个 `greet_*`、`fest_newyear`；另画了一张国庆，后来不再使用）也是本项目制作的衍生图：以 CrossPet 的设计参考图 `ref-claude.webp`、claude-pet 动作包的待机帧（比例参考）和 CrossPet 原有的同名立绘（姿势参考）为附图，在 OpenAI Codex 中生成，再由 `scripts/prepare-sprites.py` 统一大小、底边对齐并转成 WebP。沿用 CC BY-NC-SA 4.0，提示词和附图见 [docs/prompts/立绘重画-统一比例/](docs/prompts/立绘重画-统一比例/)。

动画动作（`characters/claude/anim/` 里的 80 张精灵图：替换原有姿态的待机、思考、写东西、敲命令、整理记忆、睡觉、开心、吃惊、被摸头、不耐烦、再见、哼歌、伸懒腰、打哈欠和六个节日；新加的被拎起来、闲置小动作、才艺、玩耍、吃喝、时令、偷吃零食、Claude 梗、五档余额钱袋和走路）来自 claude-pet 动作包：

- 作者：hunmer
- 来源：[hunmer/aboutme-pets](https://github.com/hunmer/aboutme-pets)（`pets/claude` 动作包），取自固定提交 `3ba083bee0ef88d9300eaa941fe2da89d0d99c7b`
- 制作方式（作者说明）：AI（Claude）生成 + 人工抠像转换，以 CrossPet 的 Claude 娘立绘 `idle.webp` 为基准
- 授权：作者 2026-11-05 在 [hunmer/aboutme-pets#1](https://github.com/hunmer/aboutme-pets/issues/1) 书面同意在 claudia-pet 中使用和再分发（放进公开仓库、打包进 Release；可裁剪、缩放、重新压缩或改格式，不改画面内容），按 CC BY-NC-SA 4.0 发布；以后新增的 Claude 动作同样适用。同一仓库的 `pets/dsh`、`pets/dsh1` 素材来自其他上游，不在这份授权里，本项目没有使用。

本项目原样使用这些精灵图文件（没有重新编码），只在播放时缩放、对齐，按每个姿态选取播放的帧段。文件清单和用途见 [characters/claude/anim/SOURCE.md](characters/claude/anim/SOURCE.md)。

| 内容 | 来源 | 授权 |
|---|---|---|
| 源代码 | claudia-pet，基于 CrossPet；`web/dsh-physics.js` 摘录自 dsh-pet | MIT；保留 claudia-pet、CrossPet 及 dsh-pet 的版权与许可声明 |
| 角色立绘、设定、台词 | CrossPet v1.3.8 | CC BY-NC-SA 4.0 |
| 抱 Clawd 图标 | 本项目以 CrossPet 素材为参考生成 | CC BY-NC-SA 4.0 |
| 碰边动作立绘（撞头、摇晃、摔倒、趴地） | 本项目以 CrossPet 素材和动作包待机帧为参考生成 | CC BY-NC-SA 4.0 |
| 碰边动作台词 | 本项目新写，加在 CrossPet 的 `character.json` 里 | CC BY-NC-SA 4.0（随所在文件） |
| 按动画比例重画的 20 张立绘 | 本项目以 CrossPet 素材和动作包待机帧为参考生成 | CC BY-NC-SA 4.0 |
| 动画动作（`characters/claude/anim/`） | hunmer，[hunmer/aboutme-pets](https://github.com/hunmer/aboutme-pets)（`pets/claude` 动作包），以 CrossPet 的 Claude 娘为基准 AI 生成 | CC BY-NC-SA 4.0（作者书面授权） |
| Clawd 形象 | Anthropic（Claude Code 使用的橙色像素螃蟹） | 权利归 Anthropic；本项目不主张任何权利 |

原始 [角色授权说明](characters/LICENSE.md) 保持不变，其中还列有原项目其他角色；这些条目是来源记录，不表示 claudia-pet 包含那些角色。当前素材范围另见 [characters/README.md](characters/README.md)。

CC BY-NC-SA 4.0 的素材标注要求署名、非商业性使用以及相同方式共享。项目作为独立仓库维护时，仍应保留相关来源和许可。

## 运行组件

- Microsoft Edge WebView2 SDK：构建时从 nuget.org 下载 `Microsoft.Web.WebView2` 1.0.3537.50，不放在仓库里；构建产物 `dist/claudia-pet` 会带上其中的 DLL 和 `WebView2Loader.dll`。这些文件按微软该包的许可（BSD 式，允许以二进制形式再分发，须附上版权声明和免责声明）分发：构建时把包里的 `LICENSE.txt`、`NOTICE.txt` 复制成程序目录下的 `WebView2-LICENSE.txt`、`WebView2-NOTICE.txt`，GitHub Release 里打包好的程序也带着这两份文件。
- .NET Framework 4.8：运行依赖，不随本项目复制发行。

## 商标与关联

「Claude」是 Anthropic 的商标，Clawd 形象的相关权利也归 Anthropic 所有。claudia-pet 是非官方同人项目，只用这些名称和形象来指代、致敬对应产品，不表示获得 Anthropic、CrossPet 或 dsh-pet 作者的官方背书。

如果你是上述任何作品的作者或权利人，对本项目的使用方式有异议，请在 GitHub 提 Issue，我们会尽快修改或移除相关内容。

## dsh-pet MIT 许可

```text
MIT License

Copyright (c) 2026 PC2005-cloud

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```
