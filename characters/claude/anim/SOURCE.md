# 动画动作来源

本文件夹里的精灵图来自 claude-pet 动作包，原样复制，没有改动或重新编码。

- 作者：hunmer
- 来源：[hunmer/aboutme-pets](https://github.com/hunmer/aboutme-pets)（`pets/claude` 动作包，`spritesheet/` 目录）
- 固定提交：`3ba083bee0ef88d9300eaa941fe2da89d0d99c7b`
- 授权：CC BY-NC-SA 4.0，作者 2026-11-05 在 [hunmer/aboutme-pets#1](https://github.com/hunmer/aboutme-pets/issues/1) 书面授权
- 制作方式（作者说明）：AI（Claude）生成 + 人工抠像转换

格式：每张 8 列 × 15 行，每帧 640×360，共 120 帧，12 fps（10 秒），有损 WebP，带透明通道。

| 文件 | 用在哪个姿态 | 播放方式 |
|---|---|---|
| 待机呼吸休闲.webp | idle 待机 | 循环 |
| 工作状态-思考冒泡.webp | thinking 思考 | 循环 |
| 写代码.webp、轻快记录.webp | writing 写东西 | 随机一张，循环 |
| 工作状态-忙碌点按.webp | running 敲命令 | 循环 |
| 工作状态-清点归档.webp | compact 整理记忆 | 循环 |
| 原地小憩沉眠.webp | sleeping 睡觉 | 播到第 76 帧后在第 28–76 帧来回（一直睡着）；被点醒或要去干活时先播第 77–119 帧（醒过来） |
| 工作状态-雀跃庆祝.webp | happy 开心 | 整段 |
| 被吓一跳.webp | surprised 吃惊 | 整段 |
| 点击回应-元气挥手 / 害羞惊讶 / 开心跃动 / 挠痒咯咯笑.webp | pat 被摸头 | 随机一张，整段 |
| 点击回应-傲娇生气.webp | annoyed 不耐烦 | 整段 |
| 点击回应-元气挥手.webp | bye 再见 | 第 40–95 帧（挥手那段） |
| 悠闲哼歌.webp | hum 哼歌 | 整段 |
| 超大伸懒腰.webp | stretch 伸懒腰 | 整段 |
| 哈欠连天.webp | yawn 打哈欠 | 整段 |
| 舞狮头.webp | fest_spring 春节 | 整段 |
| 吃汤圆.webp | fest_lantern 元宵 | 整段 |
| 吃粽子.webp | fest_dragonboat 端午 | 整段 |
| 中秋赏月吃月饼.webp | fest_midautumn 中秋 | 整段 |
| 讨糖南瓜灯.webp、萌化小幽灵.webp | fest_halloween 万圣节 | 随机一张，整段 |
| 装点圣诞树v3.webp | fest_christmas 圣诞 | 整段 |
| 被鼠标拖拽悬空反馈.webp | drag 被拎起来 | 拖着她时循环 |
| 东张西望.webp、原地敲击桌面互动.webp、原地专心玩魔方.webp、吹泡泡特效.webp | look / tap / cube / bubbles 发呆小动作 | 闲置随机，整段 |
| 书灵现世.webp、凭空生花.webp、变鸽子.webp | bookspirit / flower / dove 魔法 | 闲置随机，整段 |
| 偷吃零食被抓住.webp | snack 偷吃零食 / snack_caught 被抓包 | 偷吃：第 0–51 帧后在 34–51 帧来回；点她接第 52–119 帧；没被抓就跳到第 100–119 帧收起零食 |
| 吃Token.webp、碎碎念-对屏碎碎念.webp、玩游戏气急败坏.webp | token / mutter / gamer Claude 梗 | 闲置随机，整段 |
| 动物环绕.webp、蝴蝶蜜蜂环绕头顶开花.webp | animals / butterflies 魔法 | 闲置随机，整段 |
| 三球抛接、扑克魔术、吹笛子、小提琴演奏、优雅裙摆舞、可爱宅舞、轻快摇摆舞、整体换装试色.webp | 才艺（juggle、cards、flute、violin、dance_skirt、dance_otaku、dance_swing、dressup） | 闲置随机，整段 |
| 原地蹲下玩玩具汽车、玩水枪、下五子棋、抽陀螺、撸猫、踢毽子、骑木马、书页拍打节奏.webp | 玩耍（toycar、watergun 5–9 月、gomoku、top、cat、shuttlecock、rockinghorse、pagebeat） | 闲置随机，整段 |
| 吃午餐、吃晚餐、吃白饭.webp | 吃喝 lunch（11–14 点）、dinner（17–20 点）、rice（两段饭点） | 闲置随机，整段 |
| 涮火锅、吃大闸蟹、吃西瓜、吃糖葫芦、吃年糕.webp | 吃喝 hotpot（11–2 月饭点）、crab（9–11 月）、watermelon（6–8 月）、tanghulu（12–2 月，119 帧）、niangao（1–2 月） | 闲置随机，整段 |
| 吃青团、吃腊八粥、吃重阳糕.webp | 吃喝 qingtuan（公历 3/25–4/15，94 帧）、laba（农历腊月初一到初八）、chongyanggao（农历九月初七到十一） | 闲置随机，整段 |
| 放风筝、摇扇纳凉、被落叶淹没v2、堆雪人.webp | 时令 kite（3–4 月）、fan（6–8 月）、leaves（10–11 月；第一版结束时落叶还在，没用）、snowman（12–2 月） | 闲置随机，整段 |
| 穿针乞巧、放河灯、插茱萸赏菊.webp | 时令 qixi（农历七月初五到初七）、zhongyuan（七月十三到十五）、chongyang（九月初七到十一） | 闲置随机，整段 |
| 螃蟹走路.webp、原地漂浮踏步.webp | walk_crab / walk_float 走路 | 起步后在第 37–58 / 51–84 帧循环，同时平移窗口，左右都走 |
| 原地左转奔跑.webp | run_left 往左跑 | 起步（灵光一闪、转身）后在第 38–46 帧循环，只往左（不做镜像） |
| 余额-钱袋满溢 / 金袋叮当 / 数金皱眉 / 袋空如洗 / 分文不剩.webp | quota_full / jingle / count / empty / broke 余额五档（剩 ≥80% / ≥50% / ≥25% / ≥10% / 更少） | 余额换档时、闲置随机（当前这一档），整段 |

播放配置在 `../character.json` 的 `animations`。
