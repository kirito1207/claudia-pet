# 来源与授权

claudia-pet 作为独立项目维护，基于 [CrossPet](https://github.com/lokicorvus/crosspet)（lokicorvus，来源版本 v1.3.8），同时参考 [dsh-pet](https://github.com/PC2005-cloud/dsh-pet)（PC2005-cloud，来源版本 0.3.7）。

## 代码

项目代码采用 MIT 协议。CrossPet 的版权与许可见 [LICENSE](LICENSE) 和 [原始许可](upstream/CrossPet-LICENSE)；来源版本记录见 `upstream/CrossPet-VERSION`。

`web/dsh-physics.js` 直接摘录 dsh-pet 的 `runtime/electron-helper/shared-core.js` 中独立的物理函数；网页物理适配器按其桌面驱动方式接入。右键「动作 → 分组 → 具体动作」菜单和白底圆角的柔和菜单样式参照 dsh-pet 的交互与样式，由本项目自行实现（悬停色改为浅橙）。dsh-pet 的版权和 MIT 许可全文保留在本文末尾。

## 角色素材与图标

本项目随附的 Claude 娘立绘、角色设定、台词和展示图全部来自 CrossPet v1.3.8（`characters/claude/`），立绘与台词文件和原版一致，只把默认显示名从「Claude」改为「claudia」，并删去了不再使用的贴边立绘。按 CrossPet README「致谢与授权」一节的说明：

- 这些立绘由 CrossPet 用 AI 生图工具绘制；
- Claude 娘形象参考社区流行的 AI 娘设定，CrossPet 列出的参考为 [ai-school-op](https://github.com/lshhhhhhh/ai-school-op) 和 [openpet-ai-girls](https://github.com/AwesomeHou/openpet-ai-girls)；
- 社区 Claude 娘设定最初由谁创作，CrossPet 没有写明，本项目也未能确认。

这些素材按 CrossPet 的说明以 CC BY-NC-SA 4.0 授权，原文见 [characters/LICENSE.md](characters/LICENSE.md)。

软件图标（claudia 抱 Clawd 的单人透明头像）是本项目制作的衍生图：以 CrossPet 原四人图标中右下角的 Claude 娘头像、CrossPet 立绘 `characters/claude/clawd.webp` 为参考，在 OpenAI Codex 中用其内置的 image_gen 图像编辑工具生成，之后只做了 PNG / WebP / ICO 格式与尺寸转换。图标沿用 CC BY-NC-SA 4.0，使用的完整提示词保存在 [icon/README.md](icon/README.md)。

| 内容 | 来源 | 授权 |
|---|---|---|
| 源代码 | claudia-pet，基于 CrossPet；`web/dsh-physics.js` 摘录自 dsh-pet | MIT；保留 claudia-pet、CrossPet 及 dsh-pet 的版权与许可声明 |
| 角色立绘、设定、台词 | CrossPet v1.3.8 | CC BY-NC-SA 4.0 |
| 抱 Clawd 图标 | 本项目以 CrossPet 素材为参考生成 | CC BY-NC-SA 4.0 |
| Clawd 形象 | Anthropic（Claude Code 使用的橙色像素螃蟹） | 权利归 Anthropic；本项目不主张任何权利 |

原始 [角色授权说明](characters/LICENSE.md) 保持不变，其中还列有原项目其他角色；这些条目是来源记录，不表示 claudia-pet 包含那些角色。当前素材范围另见 [characters/README.md](characters/README.md)。

CC BY-NC-SA 4.0 的素材标注要求署名、非商业性使用以及相同方式共享。项目作为独立仓库维护时，仍应保留相关来源和许可。

## 运行组件

- Microsoft Edge WebView2 SDK：构建时从 nuget.org 下载 `Microsoft.Web.WebView2` 1.0.3537.50，不放在仓库里；构建产物 `dist/claudia-pet` 会带上其中的 DLL 和 `WebView2Loader.dll`。如果以后发布打包好的程序，这些文件按微软该包的许可分发，需要随包附上相应许可说明。
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
