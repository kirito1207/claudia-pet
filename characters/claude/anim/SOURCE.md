# 动画动作来源

本文件夹里的精灵图来自 claude-pet 动作包，原样复制，没有改动或重新编码。

- 作者：hunmer
- 来源：[hunmer/aboutme-pets](https://github.com/hunmer/aboutme-pets)（`pets/claude` 动作包，`spritesheet/` 目录）
- 固定提交：`3ba083bee0ef88d9300eaa941fe2da89d0d99c7b`；2026-11-07 加的 82 张取自 `da5a6fea396a7973af2fe2be72c804b341265988`（作者 11-06、11-07 新加的，含作者用 Gemini 生成的两批，同在 `pets/claude`、同一授权）
- 授权：CC BY-NC-SA 4.0，作者 2026-11-05 在 [hunmer/aboutme-pets#1](https://github.com/hunmer/aboutme-pets/issues/1) 书面授权
- 制作方式（作者说明）：AI（Claude）生成 + 人工抠像转换

格式：每张 8 列 × 15 行，每帧 640×360，共 120 帧，12 fps（10 秒），有损 WebP，带透明通道。

| 文件 | 用在哪个姿态 | 播放方式 |
|---|---|---|
| 待机呼吸休闲.webp | idle 待机 | 循环 |
| 工作状态-思考冒泡.webp | thinking 思考 | 循环 |
| 写代码.webp | writing 写东西（2026-11-10 起只用这张；轻快记录改给听你说） | 循环 |
| 轻快记录.webp | listening 听你说（2026-11-10 起替换静态图 listening.webp；从第 10 帧起，掏出本子记，1.8 秒后转思考） | 循环 |
| 工作状态-忙碌点按.webp | running 敲命令 | 循环 |
| 工作状态-清点归档.webp | compact 整理记忆 | 循环 |
| 原地小憩沉眠.webp | sleeping 睡觉 | 播到第 76 帧后在第 28–76 帧来回（一直睡着）；被点醒或要去干活时先播第 77–119 帧（醒过来） |
| 工作状态-雀跃庆祝.webp | happy 开心 | 整段 |
| 被吓一跳.webp | surprised 吃惊 | 整段 |
| 点击回应-元气挥手 / 害羞惊讶 / 开心跃动 / 挠痒咯咯笑.webp | pat 被摸头 | 随机一张，整段 |
| 点击回应-傲娇生气.webp | pout 点击反应（傲娇） | 整段 |
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
| 吃白饭.webp | 吃喝 rice（两段饭点） | 闲置随机，整段 |
| 涮火锅、吃大闸蟹、吃西瓜.webp | 吃喝 hotpot（11–2 月饭点）、crab（9–11 月）、watermelon（6–8 月） | 闲置随机，整段 |
| 吃青团、吃腊八粥、吃重阳糕.webp | 吃喝 qingtuan（公历 3/25–4/15，94 帧）、laba（农历腊月初一到初八）、chongyanggao（农历九月初七到十一） | 闲置随机，整段 |
| 放风筝、摇扇纳凉、被落叶淹没v2.webp | 时令 kite（3–4 月）、fan（6–8 月）、leaves（10–11 月；第一版结束时落叶还在，没用） | 闲置随机，整段 |
| 晨间刷牙 / 吃午餐 / 闻咖啡豆、冲可可 / 吃晚餐 / 拍枕头.webp | 问候 greet_morning 早上好 / greet_noon 中午好 / greet_afternoon 下午好（两张随机）/ greet_evening 晚上好 / greet_night 晚安（2026-11-10 起替换问候静态图；原来分别是闲置随机的 toothbrush、lunch、coffee、cocoa、dinner、pillow，不再单独随机，可可也不再限月份）。凌晨问候 greet_dawn 同日删掉 | 每个时段第一次打招呼，整段 |
| 堆雪人、吃糖葫芦、吃年糕.webp | fest_newyear 元旦（2026-11-10 起替换静态图 fest_newyear.webp；三段随机，播到第 118 帧——吃糖葫芦只有 119 帧；原来分别是时令 snowman、吃喝 tanghulu / niangao，不再单独随机） | 元旦打招呼，整段 |
| 穿针乞巧、放河灯、插茱萸赏菊.webp | 时令 qixi（农历七月初五到初七）、zhongyuan（七月十三到十五）、chongyang（九月初七到十一） | 闲置随机，整段 |
| 螃蟹走路.webp、原地漂浮踏步.webp | walk_crab / walk_float 走路 | 起步后在第 37–58 / 51–84 帧循环，同时平移窗口；螃蟹步身体朝左，只往左走（2026-11-07），漂浮踏步左右都走 |
| 原地左转奔跑.webp | run_left 往左跑 | 起步（灵光一闪、转身）后在第 38–46 帧循环，只往左（不做镜像）；收尾从第 69 帧（2026-11-07） |
| 余额-钱袋满溢 / 金袋叮当 / 数金皱眉 / 袋空如洗 / 分文不剩.webp | quota_full / jingle / count / empty / broke 余额五档（剩 ≥80% / ≥50% / ≥25% / ≥10% / 更少） | 余额换档时、闲置随机（当前这一档），整段 |

**2026-11-07 加的（82 张）**：

| 文件 | 用在哪个姿态 | 播放方式 |
|---|---|---|
| 工作状态-垂头叹气冒汗.webp | oops 哎呀（2026-11-07 起替换静态图 oops.webp） | 整段 |
| 拆礼物.webp | update 拆礼物（2026-11-07 起替换静态图 update.webp「抱礼物」；没有专门的触发时机，放在玩耍组闲置随机） | 整段 |
| 伸脚趾、咬指甲、抓痒、抖腿、掰手指、揉眼睛、摸肚子、甩手甩水、转手腕、转笔.webp | 发呆小动作 toes、nails、scratch、leg_bounce、finger_count、eye_rub、tummy、hand_flick、wrist_roll、pen_spin | 闲置随机，整段 |
| 吹叶笛、弹玻璃杯沿、弹空气吉他、弹空气吉他v2、弹空气吉他v3、手摇八音盒、敲木鱼、淑女屈膝礼.webp | 才艺 leaf_flute、glass_harp、air_guitar、music_box、woodfish、curtsy | 闲置随机，整段 |
| 叠叠乐、吹蒲公英、弹脑门、戳泡泡纸、打水漂、抖空竹、抛帽接帽、抛接小苹果、摇骰盅、翻花绳、踩影子、转伞、转盘子.webp | 玩耍 jenga、dandelion、forehead_flick、bubble_wrap、skip_stone、diabolo、hat_toss、apple_toss、dice、cats_cradle、shadow_step、umbrella_spin、plate_spin | 闲置随机，整段 |
| 分巧克力、刨冰屑、剥栗子、吸布丁、咬脆饼、啃玉米、喝汤吹凉、挑鱼刺、捏饭团、摇汽水瓶、烤红薯、舀蜂蜜v2.webp | 吃喝 chocolate、shaved_ice（6、7、8 月）、chestnut（9、10、11 月）、pudding、cracker、corn（7、8、9、10 月）、soup、fishbone、onigiri、soda（5、6、7、8、9 月）、sweet_potato（11、12、1、2 月）、honey | 闲置随机，整段 |
| 举手遮阳偷看、原地踏雪、听蝉、抱暖手炉、掌心接雪花、接雨滴、搓手哈气取暖、撑透明小伞、被风吹乱发型.webp | 时令 sunshade（5、6、7、8、9 月）、snow_step（12、1、2 月）、cicada（6、7、8 月）、hand_warmer（11、12、1、2 月）、snowflake（12、1、2 月）、raindrop（3、4、5、6、7 月）、warm_breath（11、12、1、2 月）、clear_umbrella（3、4、5、6、7 月）、windy（3、4、10、11 月） | 闲置随机，整段 |
| 串珠链、折小纸船、捏陶土、磨墨、编草蚱蜢.webp | 手工（新组） beads、paper_boat、clay、ink、grasshopper | 闲置随机，整段 |
| 拨算盘珠、深度思考碎碎念v4.webp | Claude 梗 abacus、mutter_lazy | 闲置随机，整段 |

2026-11-09 删掉其中 16 张（随机动作太多，随时能触发的从 96 个减到 80 个，和 dsh-pet 的 80 个相当）：发呆小动作 抠鼻子、挠脚心、揪耳朵、憋气鼓腮；才艺 打拍子数拍、按音叉、摇沙锤、敲三角铁、摇小风铃；手工 削铅笔、扎束干花、打铁小铃、描瓷纹、糊纸伞骨、绕线轴、缝布偶。要找回从作者库 `da5a6fe` 取。

圣诞 fest_christmas 加入 装点圣诞树.webp，和装点圣诞树v3 随机轮换。

作者库里这次也没用的：被落叶淹没（结束时落叶还堆在脚边（之前就弃用））；放孔明灯（后半段镜头拉近、人物出画（之前就弃用））；装点圣诞树v2（之前判断会超出窗口左边（之前就弃用））；打瞌睡被惊醒（睡着被点醒播打哈欠（用户之前定了不换））。

播放配置在 `../character.json` 的 `animations`。
