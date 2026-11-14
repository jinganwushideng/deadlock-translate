# Deadlock 英文术语、外号、简写与设定词库大全

> 本文档对齐项目词库 [terms.json](../src/Bridge/data/terms.json) 与扩展脚本 [term_extensions.py](../scripts/term_extensions.py) 编写，用于补充和完善 Deadlock 英文词库（含英雄外号/内部代号/真名、三系装备简写/旧称/Dota·LoL 类比黑话、局内机制与地图点位、世界观设定，以及词库合入建议）。
>
> **合入安全原则**：根据 [Terms.cs](../src/Bridge/Terms.cs) 与 [terms-cases.json](../tests/terms-cases.json) 的测试约束，日常英文高频词（如 `lady`、`pocket`、`seven`、`goo`、`cube`、`echo`、`carpet`、`boots`、`decay`）与双字母高歧义缩写（如 `ss`、`ms`、`tb`、`es`、`kd`、`gt`、`ap`、`tp`）必须归入 `AMBIGUOUS_ALIASES` / `AMBIGUOUS_PHRASES` / `AMBIGUOUS_HINTS`，防止日常对话被误翻。

---

## 目录

- [一、英雄（Heroes）外号、简写、内部代号与设定真名](#一英雄heroes外号简写内部代号与设定真名)
  - [1. 经典主力阵容（22 位）](#1-经典主力阵容22-位)
  - [2. 新英雄 / Old Gods, New Blood / Hero Labs 阵容](#2-新英雄--old-gods-new-blood--hero-labs-阵容)
  - [3. 测试与开发期实验英雄（已收录于 terms.json）](#3-测试与开发期实验英雄已收录于-termsjson)
- [二、装备（Items）简写、外号、旧名与 MOBA 类比词](#二装备items简写外号旧名与-moba-类比词)
  - [1. Weapon（武器系 / 橙装）](#1-weapon武器系--橙装)
  - [2. Vitality（活力系 / 绿装）](#2-vitality活力系--绿装)
  - [3. Spirit（元灵系 / 紫装）](#3-spirit元灵系--紫装)
  - [4. 常见高歧义双字母缩写速查（建议 `AMBIGUOUS_HINTS`）](#4-常见高歧义双字母缩写速查建议-ambiguous_hints)
- [三、对局机制、地图点位与身法黑话（Mechanics, Map Callouts & Slang）](#三对局机制地图点位与身法黑话mechanics-map-callouts--slang)
  - [1. 核心目标与地图资源（Objectives & Map Resources）](#1-核心目标与地图资源objectives--map-resources)
  - [2. 兵线与分路称呼（Lanes & Callouts）](#2-兵线与分路称呼lanes--callouts)
  - [3. 战斗、经济与身法术语（Combat, Economy & Movement Tech）](#3-战斗经济与身法术语combat-economy--movement-tech)
- [四、世界观与背景设定专有名词（Lore, Factions & Worldbuilding）](#四世界观与背景设定专有名词lore-factions--worldbuilding)
- [五、可直接合入 `scripts/term_extensions.py` 的参考片段](#五可直接合入-scriptsterm_extensionspy-的参考片段)

---

## 一、英雄（Heroes）外号、简写、内部代号与设定真名

### 1. 经典主力阵容（22 位）

| 标准英文名 (`terms.json`) | 项目中文名 | 建议直替别名（`ALIASES`） | 需语境触发别名（`AMBIGUOUS_ALIASES`） | 说明 / 内部代号 / 设定真名 / 类比梗 |
| :--- | :--- | :--- | :--- | :--- |
| **Abrams** | 亚伯兰 | `abram`, `blue cow`, `blue bull`, `blue guy` | `abe`, `abs`, `rams`, `cow`, `bull`, `detective`, `book guy` | 蓝皮恶魔侦探，手持魔典 |
| **Bebop** | 比波普 | `bop`, `boop`, `hook bot`, `bomb bot` | `bb`, `clanker`, `robot`, `scrap bot` | 废品屋修理工出身的钩子/炸弹机器人 |
| **Dynamo** | 奇能 | `dyna`, `dymo`, `black hole guy` | `dyn`, `prof`, `professor`, `enigma` | 纽约某大学教授，大招 `Singularity` 类比 Dota 谜团 (`Enigma`) |
| **Grey Talon** | 灰爪 | `greytalon`, `gray talon`, `owl man`, `bird man` | `gt`, `talon`, `grandpa`, `hanzo`, `orion` | 内部开发代号 **`Orion`**；年迈弓箭手，机制常被类比半藏 (`Hanzo`) |
| **Haze** | 岚梦 | `p-haze`, `sandman` | `reaper`, `sleep agent` | OSIC“睡魔 (`Sandman`)”部门特工，大招弹幕常被类比死神 (`Reaper`) |
| **Infernus** | 炽焱 | `inferno`, `flame boy`, `fire guy` | `infer`, `inf`, `fern`, `furnace`, `bartender` | 伊克西亚裔调酒师，`furnace` 为玩家语音/拼写谐音梗 |
| **Ivy** | 青藤 | `gargoyle`, `kudzu` | `imp`, `airdrop`, `stone girl` | 苏醒的石像鬼，一技能葛根炸弹常直呼 `kudzu` |
| **Kelvin** | 开尔文 | `kelv`, `ice guy`, `dome guy`, `frozone` | `kel`, `mei`, `snow guy`, `ice beam` | 冰球大招 `Frozen Shelter`（俗称 `Ice Dome`），玩家常调侃酷冰侠 (`Frozone`) |
| **Lady Geist** | 盖斯特夫人 | `geist`, `ladyg`, `lady g` | `lg`, `lady`, `milf`, `suck lady`, `siphon lady` | 右臂寄生恶魔 `Oathkeeper`，核心机制生命吸取与换血 |
| **Lash** | 劳什 | `the lash`, `whip guy`, `jacob lash` | `asshole`, `whip` | 全名 **Jacob Lash**；背景简介彩蛋名言 *“Jacob Lash is an asshole”* |
| **McGinnis** | 麦金妮 | `mcg`, `ginnis`, `mcgin`, `mcginnes`, `turret lady` | `mg`, `mgk`, `engie`, `engineer`, `torb` | 军工炮台工程师，`mgk` 为 Machine Gun Kelly 谐音 |
| **Mirage** | 蜃景 | `djinn`, `tornado guy`, `scarab guy` | `mir`, `bodyguard`, `nashala` | 怀俄明州诺玛（`Nashala` 部落相关）巨灵保镖，二技能龙卷风与圣甲虫 |
| **Mo & Krill** | 莫克双雄 | `mo and krill`, `mo&krill`, `mo n krill`, `m&k`, `mnk`, `mokrill`, `mok`, `mole guy` | `mk`, `mo`, `krill`, `mole`, `burrow`, `diglett` | 鼹鼠莫里斯与骑手克里尔二人组，常被称为 `M&K` / 地鼠 (`Diglett`) |
| **Paradox** | 悖论 | `para`, `dox`, `pdox`, `swap lady`, `time lady` | `px`, `swap`, `chrono` | 博物馆时间窃贼组织面具人，核心大招换位 `Paradoxical Swap` |
| **Pocket** | 口袋 | `pock`, `frog guy`, `briefcase guy`, `arin fairfax` | `pkt`, `arin`, `fairfax`, `frog`, `toad`, `suitcase`, `puck` | 真名 **Arin Fairfax**（费尔法克斯工业继承人），机制高机动致敬 Dota 帕克 (`Puck`) |
| **Seven** | 柒 | `sev`, `gigawatt`, `lightning guy`, `ball guy` | `7`, `svn`, `box`, `toaster` | 内部开发代号 **`Gigawatt`**；头戴电刑刑具被玩家戏称面包机 (`toaster`) |
| **Shiv** | 希弗 | `pompadour`, `monkey guy`, `bleed guy` | `monkey`, `elvis`, `greaser`, `baxter` | 巴克斯特学会成员，“猴脸”飞机头造型 (`pompadour` / `Elvis`) |
| **Vindicta** | 薇妲 | `vindi`, `vin`, `vind`, `dicta`, `crow lady`, `feet lady` | `vd`, `vita`, `sniper`, `widow`, `bird` | 塞勒姆猎巫幸存狙击手，因赤足飞行造型常被玩 `feet` 梗 |
| **Viscous** | 魔液 | `visc`, `vis`, `visk`, `goo man`, `goo guy`, `goo boy`, `cube guy`, `flubber` | `goo`, `slime`, `blob`, `cube`, `ball` | 深海绿泥意识体，核心技能无敌方块 (`The Cube`) 与魔球 (`Goo Ball`) |
| **Warden** | 沃督 | `ward`, `cop guy`, `police guy`, `cage guy` | `cop`, `bobby`, `alchemist`, `warden` | 炼金术世家猎魔人，英伦老式警服造型常被直呼 `cop` / `bobby` |
| **Wraith** | 灵魅 | `card lady`, `tk lady` | `wr`, `cards`, `gambler`, `telekinesis` | 地下博弈女王，飞牌 (`Card Trick`) 与念力大招 (`Telekinesis` / `TK`) |
| **Yamato** | 大和 | `yama`, `yam`, `mato`, `crimson slash` | `ymt`, `forehead`, `samurai`, `weeb`, `genji` | 日本黑道“第七月”双刀女武士，旧模额头较高常被戏称 `forehead` |

### 2. 新英雄 / Old Gods, New Blood / Hero Labs 阵容

| 标准英文名 (`terms.json`) | 项目中文名 | 建议直替别名（`ALIASES`） | 需语境触发别名（`AMBIGUOUS_ALIASES`） | 说明 / 内部代号 / 设定真名 |
| :--- | :--- | :--- | :--- | :--- |
| **Apollo** | 阿波罗 | `fencer`, `sun boy` | `duelist` | 剑术决斗家 |
| **Billy** | 比利 | `billy the goat`, `goat boy`, `punk goat`, `bat goat` | `goat`, `ram` | 挥舞棒球棍的朋克山羊头恶魔 |
| **Calico** | 卡厉可 | `cat lady`, `leopard` | `ava`, `cat`, `nano` | 内部代号 **`Nano`**；职业刺客，其宠物黑猫名叫 **Ava** |
| **Celeste** | 赛凌 | `unicorn girl`, `roller girl`, `dazzler` | `unicorn`, `skater` | 科尼岛独角兽轮滑表演者 |
| **Drifter** | 孤猎 | `vamp`, `creepy guy`, `blood guy` | `stalker`, `hobo`, `drifter` | 路易斯安那百年血腥流浪汉/吸血鬼猎手 |
| **Fathom** | 海魇 | `slork`, `fish guy`, `shark guy`, `deep one` | `slark`, `fish`, `frogfish` | **极高频**：前内部开发名 **`Slork`**（致敬 Dota 斯拉克 Slark），老玩家几乎只叫 `Slork` |
| **Graves** | 格瑞墓 | `darcy`, `darcy graves`, `necro`, `ghoul girl` | `necromancer`, `graves` | 全名 **Darcy Graves**，黑摩学院 (`Blackmore Academy`) 死灵法师 |
| **Holliday** | 哈雷黛 | `holli`, `holiday`, `astro`, `doc holliday`, `cowgirl`, `lasso girl` | `sheriff`, `lasso`, `kegs` | **极高频**：内部开发代号 **`Astro`**，西部女警长，跳板 (`Bounce Pad`) 与炸药桶 (`Powder Keg`) |
| **Mina** | 米娜 | `mina ha`, `vampire girl`, `umbrella girl` | `umbrella`, `vamp` | 豪门吸血鬼千金，撑伞射击 |
| **Paige** | 佩吉 | `bookworm`, `book girl`, `storyteller`, `dragon girl` | `knight`, `dragon` | 召唤书中骑士与纸龙的文学少女 |
| **Raven** | 渡鸦 | `umbrella guy`, `russian spy`, `bird guy` | `spy` | 前苏联持伞特工 |
| **Rem** | 雷姆 | `sleepy boy`, `pillow guy`, `lil helpers` | `pillow`, `familiar` | 枕头睡眠小精灵 |
| **Silver** | 西尔芙 | `lila`, `lila silver`, `wolf girl`, `wolf-girl`, `wolfgirl` | `wolf`, `werewolf`, `lycan` | 全名 **Lila Silver**，狼人赏金猎人 |
| **Sinclair** | 无双魔术师 | `the magnificent sinclair`, `bunny guy`, `hex guy` | `magician`, `henry`, `savannah`, `bunny`, `rabbit` | **Henry & Savannah Sinclair** 一体双魂魔术师，三技能 `Rabbit Hex` 变兔子 |
| **The Doorman** | 门侍 | `doorman`, `door man`, `door guy`, `bellhop`, `hotel guy`, `portal guy` | `portal`, `luggage`, `cart` | 男爵夫人酒店 (`Baroness Hotel`) 门童，传送门与行李车大招 |
| **Trapper** | 陷阱师 | `trap guy`, `pest control`, `exterminator`, `web guy` | `spider`, `web` | 纽约害虫防治/精神蛛网陷阱师 |
| **Venator** | 诛邪者 | `father quinn`, `quinn rourke`, `vatican guy`, `crossbow priest` | `priest`, `quinn`, `hunter` | 真名 **Father Quinn Rourke**，梵蒂冈驱魔神父 |
| **Victor** | 维克多 | `frankenstein`, `zombie guy`, `undead guy`, `self revive` | `vic`, `zombie`, `franken` | 弗兰肯斯坦式缝合怪 |
| **Vyper** | 蝰邪 | `vyp`, `viper`, `kali`, `slide girl` | `snake`, `snek`, `slither` | 内部开发代号 **`Kali`**；滑铲流毒蛇暴徒 |
| **Wrecker** | 破坏王 | `wreck`, `junk guy`, `scrap guy` | `butcher` | 吞噬野怪/废料并投掷破坏球的怪物 |

### 3. 测试与开发期实验英雄（已收录于 `terms.json`）

以下角色名称已存在于 [terms.json](../src/Bridge/data/terms.json)（游戏资源/Hero Labs/早期代号），英文玩家偶在拆包与测试服讨论中提及：

| 标准英文名 (`terms.json`) | 项目中文名 | 说明 |
| :--- | :--- | :--- |
| **Akimbo** | 阿金驳 | 双持枪械实验英雄代号 |
| **Baba** | 婆婆 | 实验英雄代号（巫婆题材） |
| **Boho** | 波米 | 实验英雄代号 |
| **Deadman Danny** | 空骨丹尼 | 实验英雄/骷髅船长题材代号 |
| **Nurse Harrow** | 亥芮护士 | 实验英雄代号 |
| **Rat King** | 鼠王 | 早期下水道鼠王实验代号 |
| **Shield Guy** | 盾男 | 早期原型代号（部分机制衍化为早期坦位） |
| **Skyrunner** | 御空行者 | 早期空中滑索机动代号 |
| **Solomon** | 所罗门 | 实验英雄代号 |
| **Swan** | 天鹅舞伶 | 实验英雄代号 |
| **Violet** | 阿紫 | 实验英雄代号 |

---

## 二、装备（Items）简写、外号、旧名与 MOBA 类比词

Deadlock 于 2025 年 5 月经历商店重做（Shop Rework），大量装备改名或合并。欧美社区玩家与老攻略至今高频混用**改名前旧英文名 (`LEGACY_ENGLISH_NAMES`)**、**首字母缩写**以及 **Dota 2 / LoL 同机制外号**。

> 注意：下表所有“标准英文名”和“项目中文名”均严格对照 [terms.json](../src/Bridge/data/terms.json) 实测结果。

### 1. Weapon（武器系 / 橙装）

| 标准英文名 (`terms.json`) | 项目中文名 | 改名前旧英文名 / 历史名 | 英文简写 / 外号 / 类比黑话 | 建议归类 |
| :--- | :--- | :--- | :--- | :--- |
| **High-Velocity Rounds** | 高速弹 | `High-Velocity Mag` | `hvr`, `hvm`, `high velocity`, `velocity rounds` | 旧名放 `LEGACY_ENGLISH_NAMES`；`hvr`/`hvm` 可直替 |
| **Rapid Rounds** | 快手连发 | — | `rr` | `rr` 须 `AMBIGUOUS` |
| **Extended Magazine** | 扩容弹匣 | `Basic Magazine` | `ext mag`, `basic mag` | 旧名放 `LEGACY_ENGLISH_NAMES` |
| **Titanic Magazine** | 巨型弹匣 | — | `titanic mag`, `big mag`, `titanic` | `titanic` 建议 `AMBIGUOUS` |
| **Close Quarters** | 近身决斗 | — | `cq`, `close quarter` | `cq` 须 `AMBIGUOUS` |
| **Point Blank** | 近身猛击 | — | `pb` | `pb` 须 `AMBIGUOUS` |
| **Headshot Booster** | 头弹奖励 | — | `hsb`, `hs booster`, `headshot boost` | `hsb` 可直替 |
| **Headhunter** | 猎头 | — | `hh` | `hh` 须 `AMBIGUOUS` |
| **Crippling Headshot** | 头弹破防 | — | `crippling`, `cripple hs`, `chs` | `chs`/`crippling` 可直替 |
| **Weakening Headshot** | 头弹弱防 | — | `whs`, `weakening hs` | `whs` 可直替 |
| **Hollow Point** | 空尖弹 | `Hollow Point Ward` | `hpw`, `hollow point ward` | 旧名放 `LEGACY_ENGLISH_NAMES`；`hpw` 可直替 |
| **Monster Rounds** | 猎怪弹 | — | `mr`, `pve rounds` | `mr` 须 `AMBIGUOUS` |
| **Cultist Sacrifice** | 特异供奉 | — | `cultist`, `eat creep`, `midas` | Dota 点金手 (`Midas`) 类比；`midas` 建议 `AMBIGUOUS` |
| **Active Reload** | 高速装填 | — | `ar`, `gears reload` | `ar` 须 `AMBIGUOUS` |
| **Kinetic Dash** | 动能冲刺 | — | `kd`, `kdash`, `kinetic` | `kd` 已在 `AMBIGUOUS_HINTS` |
| **Fleetfoot** | 轻盈飞步 | `Fleetfoot Boots` | `fleetfoot boots`, `fleet`, `phase boots` | `fleetfoot boots` 直替；`fleet` 建议 `AMBIGUOUS` |
| **Mystic Shot** | 秘术射击 | — | `ms`, `mshot` | `mshot` 直替；`ms` 已在 `AMBIGUOUS_HINTS` |
| **Slowing Bullets** | 减速弹 | — | `sb`, `slow bullets` | `sb` 须 `AMBIGUOUS` |
| **Restorative Shot** | 疗愈子弹 | — | `resto shot`, `heal shot` | 可直替 |
| **Opening Rounds** | 先发强袭 | `Pristine Emblem` (机制前身) | `pristine`, `pristine emblem`, `first strike` | `pristine emblem` 可放 `ALIASES` |
| **Split Shot** | 裂光射击 | — | `splitshot`, `multishot` | 可直替 |
| **Backstabber** / **Ambush** | *(如有新增潜行系)* | — | — | 以游戏资源为准 |
| **Intensifying Magazine** | 火力渐升 | `Intensifying Clip` | `imag`, `intensifying mag`, `intensifying clip`, `intensifying` | 旧名放 `LEGACY_ENGLISH_NAMES`；`imag` 可直替 |
| **Escalating Resilience** | 层层防御 | — | `er`, `esc res` | `er` 须 `AMBIGUOUS` |
| **Berserker** | 狂战士 | — | `zerk`, `berzerker` | `zerk` 可直替 |
| **Burst Fire** | 健步疾射 | — | `bf` | `bf` 须 `AMBIGUOUS` |
| **Long Range** | 远击威力 | — | `lr` | `lr` 须 `AMBIGUOUS` |
| **Sharpshooter** | 弹无虚发 | — | `sharpshooter` | — |
| **Hunter's Aura** | 猎人光环 | — | `hunters aura`, `ha` | `hunters aura` 直替；`ha` 须 `AMBIGUOUS` |
| **Heroic Aura** | 英雄光环 | — | `heroic` | `heroic` 建议 `AMBIGUOUS` |
| **Alchemical Fire** | 炼金之火 | — | `alch fire`, `alch`, `molly`, `molotov` | `alch fire` 直替；`molly` 建议 `AMBIGUOUS` |
| **Tesla Bullets** | 特斯拉弹 | — | `tesla`, `maelstrom`, `mjollnir`, `chain lightning` | `tesla` 可直替；`maelstrom` 建议 `AMBIGUOUS` |
| **Toxic Bullets** | 毒弹 | — | `tb`, `toxic ammo`, `toxic`, `bleed bullets`, `poison bullets` | `toxic` 建议 `AMBIGUOUS`；`tb` 已在 `AMBIGUOUS_HINTS` |
| **Spirit Shredder** | 碎灵子弹 | `Spirit Shredder Bullets`, `Soul Shredder Bullets` | `ssb`, `spirit shredder bullets`, `soul shredder bullets`, `shredder bullets` | 旧名已在 `LEGACY_ENGLISH_NAMES`；`ssb` 可直替 |
| **Spellslinger** | 施术之手 | `Spellslinger Headshots`, `Spelldrinker` | `spellslinger headshots`, `spelldrinker` | 旧名建议放 `LEGACY_ENGLISH_NAMES` |
| **Capacitor** | 积雷电容 | — | `cap`, `nullifier` | Dota 否决 (`Nullifier`，驱散敌方增益)；`cap`/`nullifier` 建议 `AMBIGUOUS` |
| **Shadow Weave** | 来去无踪 | — | `sw`, `shadowblade`, `shadow blade`, `invis` | Dota 隐刀类比；`shadowblade` 直替，`sw`/`invis` 建议 `AMBIGUOUS` |
| **Glass Cannon** | 玻璃大炮 | — | `gc`, `glass`, `rapier`, `divine rapier` | `gc` 可直替；`glass` 须 `AMBIGUOUS` |
| **Lucky Shot** | 幸运一击 | — | `ls`, `lucky`, `crit` | 全部建议 `AMBIGUOUS` |
| **Ricochet** | 跳弹射击 | — | `rico`, `bouncing bullets` | `rico` 可直替 |
| **Armor Piercer** | 穿甲弹 | `Armor Piercing Rounds` (常见误叫) | `apr`, `ap rounds`, `armor piercing rounds`, `ap` | `ap rounds`/`apr` 可直替；`ap` 须 `AMBIGUOUS` |
| **Siphon Bullets** | 虹吸弹 | — | `siphon`, `max hp steal` | `siphon` 建议 `AMBIGUOUS`（与 Geist 技能 `Siphon Life` 重名） |
| **Silencer** | 沉默子弹 | — | `silence bullets`, `orchid bullets` | `silence bullets` 可直替 |
| **Spiritual Overflow** | 元灵漫溢 | — | `so`, `overflow` | 均须 `AMBIGUOUS` |
| **Frenzy** | 狂乱 | — | `frenzy` | — |
| **Vampiric Burst** | 疗愈爆发 | — | `vb`, `vamp burst`, `satanic` | Dota 撒旦之邪力 (`Satanic`) 类比；`vb` 须 `AMBIGUOUS` |
| **Warp Stone** | 传送石 | — | `warpstone`, `blink` | Dota 跳刀 (`Blink`) 类比；`warpstone` 已在 `ALIASES` |
| **Weighted Shots** | 重压射击 | — | `weighted`, `heavy shots` | `weighted` 建议 `AMBIGUOUS` |
| **Express Shot** | 疾速射击 | — | `express` | 建议 `AMBIGUOUS` |
| **Crushing Fists** | 粉碎重拳 | — | `fists`, `melee shred` | 建议 `AMBIGUOUS` |

### 2. Vitality（活力系 / 绿装）

| 标准英文名 (`terms.json`) | 项目中文名 | 改名前旧英文名 / 历史名 | 英文简写 / 外号 / 类比黑话 | 建议归类 |
| :--- | :--- | :--- | :--- | :--- |
| **Extra Health** | 额外生命 | — | `eh`, `green hp` | `eh` 须 `AMBIGUOUS` |
| **Extra Regen** | 加速回复 | — | `regen` | 已在 `AMBIGUOUS_PHRASES` |
| **Extra Stamina** | 耐力补给 | — | `es`, `extra stam`, `extra stem` | `es` 已在 `AMBIGUOUS_HINTS` |
| **Sprint Boots** | 疾跑靴 | — | `boots`, `brown boots` | `boots` 须 `AMBIGUOUS` |
| **Enduring Speed** | 疾速不怠 | — | `es`, `enduring`, `green boots`, `upgraded boots` | `es` 已在 `AMBIGUOUS_HINTS` |
| **Stamina Mastery** | 跑酷大师 | `Superior Stamina` (旧称合并) | `sup stam`, `superior stamina`, `parkour` | `superior stamina` 放 `LEGACY_ENGLISH_NAMES` |
| **Healing Rite** | 治疗仪式 | — | `rite`, `salve`, `healing salve` | Dota 大药罐 (`Healing Salve`)；`rite`/`salve` 建议 `AMBIGUOUS` |
| **Healing Booster** | 治疗强化 | — | `heal boost`, `hb` | `hb` 须 `AMBIGUOUS` |
| **Healing Nova** | 治疗之环 | `Health Nova` | `health nova`, `heal nova`, `nova`, `mek`, `mekansm` | `health nova` 放 `LEGACY_ENGLISH_NAMES`；Dota 梅肯类比 |
| **Healing Tempo** | 治疗律动 | — | `tempo` | 建议 `AMBIGUOUS` |
| **Restorative Locket** | 疗愈护符 | — | `locket`, `wand`, `magic wand`, `stick` | Dota 魔棒 (`Magic Wand`) 类比，均建议 `AMBIGUOUS` |
| **Melee Lifesteal** | 近战疗法 | — | `ml`, `melee ls` | `ml` 须 `AMBIGUOUS` |
| **Bullet Lifesteal** | 子弹回复 | — | `bls`, `gun lifesteal` | `bls` 可直替 |
| **Spirit Lifesteal** | 元灵吸收 | — | `sls`, `spell lifesteal` | `sls` 可直替 |
| **Bullet Armor** | 子弹护甲 | — | `ba`, `orange armor`, `gun armor` | `ba` 须 `AMBIGUOUS` |
| **Bullet Resilience** | 子弹坚甲 | `Improved Bullet Armor` | `iba`, `improved bullet armor`, `bullet res` | 旧名已在 `LEGACY_ENGLISH_NAMES`；`iba` 可直替 |
| **Spirit Armor** | 元灵护甲 | — | `sa`, `purple armor`, `magic armor` | `sa` 须 `AMBIGUOUS` |
| **Spirit Resilience** | 元灵护体 | `Improved Spirit Armor` | `isa`, `improved spirit armor`, `spirit res` | 旧名已在 `LEGACY_ENGLISH_NAMES`；`isa` 可直替 |
| **Debuff Reducer** | 减益缩短 | — | `dr`, `debuff red` | `dr` 须 `AMBIGUOUS` |
| **Dispel Magic** | 驱散魔法 | `Debuff Remover` | `debuff remover`, `remover`, `dispeller`, `cleanse`, `manta` | **极高频**：`Debuff Remover` 必入 `LEGACY_ENGLISH_NAMES` |
| **Enchanter's Emblem** | 附魔师纹章 | `Enchanter's Barrier` | `enchanters barrier`, `enchanter's barrier`, `enchanters`, `spirit shield` | 旧名放 `LEGACY_ENGLISH_NAMES` |
| **Battle Vest** | 战斗背心 | `Combat Barrier` | `combat barrier`, `gun shield` | 旧名放 `LEGACY_ENGLISH_NAMES` |
| **Guardian Ward** | 护卫结界 | `Savior` (概念前身) | `savior` | 建议 `AMBIGUOUS` |
| **Divine Barrier** | 神圣屏障 | — | `db`, `barrier`, `glimmer` | `db`/`barrier` 须 `AMBIGUOUS` |
| **Reactive Barrier** | 应急屏障 | — | `rb`, `reactive` | `rb`/`reactive` 须 `AMBIGUOUS` |
| **Rescue Beam** | 营救光束 | — | `beam`, `pull beam`, `rescue` | `beam`/`rescue` 须 `AMBIGUOUS` |
| **Return Fire** | 回应射击 | — | `rf`, `blademail`, `blade mail`, `bm`, `thorns` | Dota 刃甲 (`Blade Mail`)；`blademail` 可直替，`rf`/`bm` 须 `AMBIGUOUS` |
| **Healbane** | 不治魔咒 | — | `heal bane`, `hb`, `antiheal`, `anti-heal` | `heal bane` 已在 `ALIASES`；`hb` 须 `AMBIGUOUS` |
| **Fortitude** | 坚毅之心 | — | `fort`, `heart`, `tarrasque` | Dota 龙心 (`Heart of Tarrasque`) 类比；`fort`/`heart` 须 `AMBIGUOUS` |
| **Majestic Leap** | 升空飞跃 | — | `leap`, `mleap`, `ml` | `mleap` 可直替；`leap`/`ml` 须 `AMBIGUOUS` |
| **Metal Skin** | 铜皮铁骨 | — | `ms`, `metal`, `metal-skin`, `ghost scepter`, `ghost` | Dota 绿杖 (`Ghost Scepter`) 类比；`ms` 已在 `AMBIGUOUS_HINTS` |
| **Counterspell** | 法术反制 | — | `cs`, `parry spell`, `linkens`, `lotus` | Dota 林肯/莲花球类比；`cs` 须 `AMBIGUOUS` |
| **Spellbreaker** | 破咒护符 | — | `sb`, `magebane` | `sb` 须 `AMBIGUOUS` |
| **Plated Armor** | 铁板重甲 | — | `plated`, `plate armor` | `plated` 建议 `AMBIGUOUS` |
| **Juggernaut** | 破阵之势 | — | `jug`, `jugg` | `jug` 须 `AMBIGUOUS` |
| **Trophy Collector** | 寻猎季节 | — | `trophy`, `tc`, `mejai` | LoL 杀人书 (`Mejai`) 类比；均建议 `AMBIGUOUS` |
| **Veil Walker** | 穿幕行者 | — | `vw`, `veil`, `cosmic veil` | 均建议 `AMBIGUOUS` |
| **Fury Trance** | 怒意之潮 | `Fervor` (旧称) | `fervor`, `mom`, `mask of madness` | Dota 疯狂面具 (`Mask of Madness`)；`fervor` 建议 `AMBIGUOUS` |
| **Blood Tribute** | 殷红贡礼 | — | `bt`, `armlet` | Dota 臂章 (`Armlet`) 类比；`bt`/`armlet` 建议 `AMBIGUOUS` |
| **Colossus** | 巨人 | — | `col`, `big item` | `col` 须 `AMBIGUOUS` |
| **Phantom Strike** | 幻影突袭 | — | `ps`, `phantom`, `dagger` | `ps`/`phantom` 须 `AMBIGUOUS` |
| **Unstoppable** | 势不可挡 | — | `unstop`, `unstopp`, `bkb`, `black king bar` | **极高频**：玩家几乎只叫 `bkb` 或 `unstop`；`bkb` 已在 `ALIASES`/`AMBIGUOUS` |
| **Inhibitor** | 抑制术 | — | `inhib`, `dmg reduction` | `inhib` 也指兵营/圣龛，建议 `AMBIGUOUS` |
| **Leech** | 赤蟥 | — | `omnivamp`, `omni vamp` | `leech` 本身太常见，保持原名精确匹配或 `AMBIGUOUS` |
| **Cheat Death** | 幸免于难 | `Soul Rebirth` (前身重做) | `cd`, `soul rebirth`, `rebirth`, `aegis`, `ga` | LoL 复活甲 (`GA`) / Dota 不朽盾 (`Aegis`)；`cd` 勿直替（Cooldown） |
| **Witchmail** | 巫师护甲 | — | `witch mail`, `cd reflect` | `witch mail` 可直替 |

### 3. Spirit（元灵系 / 紫装）

| 标准英文名 (`terms.json`) | 项目中文名 | 改名前旧英文名 / 历史名 | 英文简写 / 外号 / 类比黑话 | 建议归类 |
| :--- | :--- | :--- | :--- | :--- |
| **Extra Spirit** | 灵力扩增 | — | `es`, `purple spirit` | `es` 已在 `AMBIGUOUS_HINTS` |
| **Improved Spirit** | 灵力高涨 | — | `is`, `imp spirit` | **`is` 绝对不可放进直替** |
| **Boundless Spirit** | 灵力无边 | — | `bs`, `boundless`, `rabadon`, `dcap` | LoL 帽子 (`Deathcap`)；均建议 `AMBIGUOUS` |
| **Extra Charge** | 额外充能 | — | `ec` | `ec` 须 `AMBIGUOUS` |
| **Rapid Recharge** | 火速充能 | — | `rr`, `rapid` | `rr`/`rapid` 须 `AMBIGUOUS` |
| **Mystic Burst** | 秘术爆发 | — | `mb`, `mburst` | `mburst` 直替；`mb` 须 `AMBIGUOUS` |
| **Tankbuster** | 重装克星 | `Improved Burst` | `tb`, `improved burst`, `ib`, `max hp burst` | **极高频**：`Improved Burst` 必入 `LEGACY_ENGLISH_NAMES`；`tb` 已在 `AMBIGUOUS_HINTS` |
| **Mystic Expansion** | 秘术扩张 | `Mystic Reach` | `mystic reach`, `me`, `mreach` | `Mystic Reach` 必入 `LEGACY_ENGLISH_NAMES`；`me` 绝对不可直替 |
| **Greater Expansion** | 强效扩张 | `Improved Reach` | `improved reach`, `ir`, `ge`, `greach` | `Improved Reach` 必入 `LEGACY_ENGLISH_NAMES` |
| **Compress Cooldown** | 冷却压缩 | `Improved Cooldown` | `improved cooldown`, `ic`, `cdr` | `Improved Cooldown` 必入 `LEGACY_ENGLISH_NAMES` |
| **Superior Cooldown** | 超速冷却 | — | `sc`, `sup cd`, `superior cd` | `sup cd` 直替；`sc` 须 `AMBIGUOUS` |
| **Transcendent Cooldown** | 超凡冷却 | — | `tc`, `trans cd`, `octarine` | Dota 玲珑心 (`Octarine`) 类比；`tc` 须 `AMBIGUOUS` |
| **Duration Extender** | 余威回荡 | — | `de`, `dur ext` | `de` 须 `AMBIGUOUS` |
| **Superior Duration** | 余威久久 | — | `sd`, `sup dur`, `superior dur` | `sup dur` 直替；`sd` 须 `AMBIGUOUS` |
| **Quicksilver Reload** | 魔力装填 | — | `qsr`, `quicksilver`, `qs reload` | `qsr`、`quicksilver` 已在 `ALIASES` |
| **Surge of Power** | 灵能涌动 | — | `sop`, `surge` | `sop` 可直替；`surge` 建议 `AMBIGUOUS` |
| **Spirit Strike** | 大伤元气 | — | `ss`, `sstrike` | `ss` 已在 `AMBIGUOUS_HINTS` |
| **Spirit Snatch** | 元灵收割 | — | `snatch`, `ssnatch` | `ssnatch` 直替；`snatch` 建议 `AMBIGUOUS` |
| **Bullet Resist Shredder** | 粉碎护甲 | — | `brs`, `bullet shredder` | `brs` 已在 `ALIASES` |
| **Mystic Vulnerability** | 秘术脆弱 | — | `mv`, `mystic vuln` | `mv` 须 `AMBIGUOUS` |
| **Escalating Exposure** | 伤上加伤 | — | `ee`, `esc exp`, `exposure` | `ee` 已在 `ALIASES`；`exposure` 建议 `AMBIGUOUS` |
| **Decay** | 衰变 | — | `urn`, `spirit vessel`, `antiheal dot` | Dota 大骨灰 (`Spirit Vessel`) 类比；建议 `AMBIGUOUS` |
| **Slowing Hex** | 减速魔咒 | — | `shex`, `slow hex`, `atos`, `rod of atos` | Dota 阿托斯之棍 (`Atos`) 类比；`shex`/`slow hex` 可直替 |
| **Disarming Hex** | 缴械魔咒 | `Withering Whip` | `withering whip`, `dhex`, `disarm hex`, `halberd` | `Withering Whip` 放 `LEGACY_ENGLISH_NAMES`；Dota 天堂之戟 (`Halberd`) |
| **Silence Wave** | 沉默之潮 | `Silence Glyph` | `silence glyph`, `swave`, `sg` | `Silence Glyph` 已在 `LEGACY_ENGLISH_NAMES` |
| **Focus Lens** | 聚焦透镜 | — | `fl`, `orchid`, `bloodthorn` | Dota 紫苑/血棘 (`Orchid`/`Bloodthorn`)；`orchid` 常用作黑话 |
| **Cold Front** | 冰天雪地 | — | `cf`, `ice nova`, `frost nova` | `cf` 须 `AMBIGUOUS` |
| **Arctic Blast** | 极地冰暴 | — | `ab`, `shivas`, `shiva` | Dota 希瓦的守护 (`Shiva's Guard`)；`shivas` 可直替 |
| **Knockdown** | 天锤压顶 | — | `kd`, `anvil`, `knock down item` | `kd` 已在 `AMBIGUOUS_HINTS`；掉落铁砧动画俗称 `anvil` |
| **Torment Pulse** | 痛苦脉冲 | — | `tp`, `torment`, `radiance` | Dota 辉耀 (`Radiance`)；`tp` 须 `AMBIGUOUS`（常指传送 Teleporter） |
| **Ethereal Shift** | 身躯虚化 | — | `es`, `eshift`, `e-shift`, `phase shift`, `zhonya`, `zhonyas`, `euls` | **极高频**：LoL 金身 (`Zhonya`) / Dota 吹风 (`Euls`)；`es` 已在 `AMBIGUOUS_HINTS` |
| **Mystic Reverb** | 秘术余波 | — | `reverb`, `mr` | `reverb` 已在 `ALIASES`；`mr` 须 `AMBIGUOUS` |
| **Cursed Relic** | 天谴圣物 | `Curse` | `curse`, `curse item`, `hex`, `sheepstick`, `vyse`, `scythe of vyse` | **极高频**：`Curse` 必入 `LEGACY_ENGLISH_NAMES`；Dota 羊刀 (`Sheepstick`/`Vyse`) |
| **Echo Shard** | 回音碎片 | — | `echo`, `echo-shard`, `eshard`, `es` | `eshard` 直替；`echo` 建议 `AMBIGUOUS`；`es` 已在 `AMBIGUOUS_HINTS` |
| **Magic Carpet** | 魔毯 | — | `carpet`, `aladdin` | 建议 `AMBIGUOUS` |
| **Refresher** | 刷新环 | — | `refresh`, `refresh orb`, `refresher orb` | `refresh orb`/`refresher orb` 可直替 |
| **Diviner's Kevlar** | 金刚宝衫 | — | `dk`, `diviners`, `kevlar` | `diviners` 可直替；`dk`/`kevlar` 建议 `AMBIGUOUS` |
| **Vortex Web** | 雷织漩涡 | — | `vortex`, `vacuum` | Dota 黑暗贤者真空 (`Vacuum`)；建议 `AMBIGUOUS` |
| **Lightning Scroll** | 雷鸣卷轴 | — | `ls`, `scroll` | `ls`/`scroll` 须 `AMBIGUOUS` |
| **Arcane Surge** | 秘法涌动 | — | `arcane surge` | 精力跃迁增幅技能范围与元灵 |
| **Mystic Conduit** | 秘术管道 | — | `conduit` | 建议 `AMBIGUOUS` |
| **Infuser** | 灵力灌注 | — | `infuse` | 建议 `AMBIGUOUS` |
| **Rusted Barrel** | 锈蚀枪管 | — | `barrel`, `rust barrel` | `barrel` 须 `AMBIGUOUS` |
| **Suppressor** | 元灵压制 | — | `supp` | 降射速装备 |

### 4. 常见高歧义双字母缩写速查（建议 `AMBIGUOUS_HINTS`）

| 缩写 | 常见多重含义（英语游戏黑话 / 装备 / 英雄） |
| :--- | :--- |
| **`ss`** | ① 敌人消失（Missing / `ss`）；② **Swift Striker**（迅捷突击）；③ **Spirit Strike**（大伤元气）；④ **Superior Stamina**（旧称） |
| **`ms`** | ① 移动速度（Movement Speed）；② 延迟（Ping ms）；③ **Metal Skin**（铜皮铁骨）；④ **Mystic Shot**（秘术射击） |
| **`tb`** | ① **Toxic Bullets**（毒弹）；② **Tankbuster**（重装克星） |
| **`es`** | ① **Echo Shard**（回音碎片）；② **Ethereal Shift**（身躯虚化）；③ **Extra Stamina**（耐力补给）；④ **Enduring Speed**（疾速不怠）；⑤ **Extra Spirit**（灵力扩增） |
| **`kd`** | ① 击杀死亡比（K/D）；② **Knockdown**（天锤压顶）；③ **Kinetic Dash**（动能冲刺） |
| **`ap`** | ① **Armor Piercer**（穿甲弹）；② 技能点数（Ability Point，升级技能用） |
| **`tp`** | ① 传送门/传送（Teleporter）；② **Torment Pulse**（痛苦脉冲） |
| **`dr`** | ① **Debuff Reducer**（减益缩短）；② 伤害减免（Damage Reduction） |
| **`ls`** | ① 吸血（Lifesteal）；② **Lucky Shot**（幸运一击）；③ **Lightning Scroll**（雷鸣卷轴） |
| **`sb`** | ① **Slowing Bullets**（减速弹）；② **Spellbreaker**（破咒护符） |
| **`mr`** | ① **Monster Rounds**（猎怪弹）；② **Mystic Reverb**（秘术余波）；③ 魔抗（Magic Resist，MOBA 通用黑话） |
| **`db`** | ① **Divine Barrier**（神圣屏障）；② 破防/减益（Debuff） |
| **`tc`** | ① **Transcendent Cooldown**（超凡冷却）；② **Trophy Collector**（寻猎季节） |
| **`mk`** | ① **Mo & Krill**（莫克双雄）；② 猴王（游戏类比） |
| **`gt`** | ① **Grey Talon**（灰爪）；② 滚蛋（网络略语，须语境保护） |

---

## 三、对局机制、地图点位与身法黑话（Mechanics, Map Callouts & Slang）

适合直接补充进 [term_extensions.py](../scripts/term_extensions.py) 的 `PHRASES` 与 `AMBIGUOUS_PHRASES` 字典。

### 1. 核心目标与地图资源（Objectives & Map Resources）

| 英文术语 / 黑话 / 短语 | 中文译名 / 局内含义 | 说明与备注 |
| :--- | :--- | :--- |
| `Urn` / `Soul Urn` / `deliver urn` / `run urn` | 灵瓮 / 交灵瓮 / 运送灵瓮 | 10 分钟首次刷新的重要团队经济目标 |
| `Mid Boss` / `midboss` / `rosh` / `roshan` / `baron` | 中路首领 / 打肉山 / 打大龙 | 欧美与俄服玩家最常用 Dota `rosh` 或 LoL `baron` 代指打中立首领 |
| `Boss Pit` / `Mid Pit` / `in pit` | 首领坑 / 中坑 | 地图正下方 Mid Boss 坑位 |
| `Rejuvenator` / `rejuv` / `reju` / `rejuve` / `aegis` | 复生石 / 水晶 | 击败首领后掉落，须使用重近战 (`heavy melee`) 击破获取 |
| `steal rejuv` / `punch rejuv` / `parry rejuv` | 抢复生石 / 拳抢水晶 / 振刀抢水晶 | 围绕掉落水晶抢夺的高频指挥语 |
| `Guardian` / `T1` / `tier 1` | 卫士 / 一塔 | 各路外塔 |
| `Walker` / `T2` / `tier 2` / `walker stomp` | 机甲 / 二塔 / 机甲践踏 | 四足移动防御塔，自带近身践踏眩晕 |
| `Base Guardians` / `T3` / `dual guardians` | 基地卫士 / 高地塔 | 基地高地门前双塔 |
| `Shrine` / `Shrines` / `rax` / `barracks` | 圣龛 / 兵营 | 摧毁两座圣龛后守护神解除无敌状态 |
| `Patron` / `Weakened Patron` / `pit patron` / `throne` / `ancient` | 守护神 / 虚弱的守护神 / 基地主水晶 | 二阶段掉入基地中央深坑后称为 `Weakened Patron` |
| `Sinner's Sacrifice` / `Sinners Sacrifice` / `sinners` / `slot machine` / `slots` / `arcade` | 罪人献祭 / 老虎机 | 地下室拳击赌博机，亮起满槽重拳 (`jackpot` / `heavy hit`) 可拿 3+ 金像永久增益 |
| `Golden Statue` / `gold statue` / `statue buff` / `crates` / `boxes` | 金雕像增益 / 打箱子 | 地图中击碎获取随机属性 Buff 与少量魂量的木箱和金像 |
| `Bridge Buff` / `bridge buffs` / `powerup` / `runes` | 桥上增益符 / 河道符 | 5 分钟起在中央高架桥两翼刷新的 4 种临时增益（枪伤/元灵/生存/移速） |
| `Teleporter` / `tp` / `subway` / `metro` | 传送门 / 地铁传送 | 地下/两侧跨线传送点 |
| `Zipline` / `zip` / `transit line` / `zip boost` / `boost zip` | 滑索 / 滑索加速 | 玩家回线轨道；`zip boost` 冷却极长，常用来赶高地团或抢目标 |
| `shoot off zip` / `zip knockdown` | 把人从滑索上打下来 | 刚上滑索无护盾保护时受击会被直接击晕坠地 |
| `Secret Shop` / `hidden shop` / `mid shop` | 神秘商店 / 中路商店 | 中路地下或侧路商店 |
| `Jump Pad` / `bounce pad` / `air vent` / `vent` / `fan` | 弹跳板 / 通风口 | 地图屋顶或地面的气柱/弹射装置 |

### 2. 兵线与分路称呼（Lanes & Callouts）

| 英文术语 / 短语 | 中文译名 / 局内含义 | 说明与备注 |
| :--- | :--- | :--- |
| `Yellow` (`York`), `Green` (`Greenwich`), `Blue` (`Broadway`), `Purple` (`Park`) | 黄路（约克）/ 绿路（格林威治）/ 蓝路（百老汇）/ 紫路（公园） | 早期 4 路旧街道名与颜色代称；3 路改版后常规称呼为 `Yellow`、`Blue`、`Green` 或 `Left`/`Mid`/`Right` |
| `Outer Lane` / `side lane` | 边路 / 外线 | 最左/最右两路 |
| `Inner Lane` / `mid lane` | 中路 / 内线 | 中央路线（3 路版中线，4 路版内双线） |
| `solo lane` / `duo lane` | 单人路 / 双人路 | 对线期分路呼号 |
| `swap lanes` / `lane swap` | 换线 | 对线被克制时请求换路 |
| `freeze lane` / `shove wave` / `crash wave` / `cut wave` | 控线 / 快速清线 / 推线进塔 / 断线 | 兵线运营战术词汇 |
| `Trooper` / `creeps` / `minions` / `wave` | 步兵 / 小兵 / 兵线 | 默认称 `Trooper` 或沿用 Dota `creeps` |

### 3. 战斗、经济与身法术语（Combat, Economy & Movement Tech）

| 英文术语 / 简写 / 黑话 | 中文译名 / 局内含义 | 说明与备注 |
| :--- | :--- | :--- |
| `Soul` / `Souls` / `Soul Orb` / `orbs` | 魂量 / 灵魂球 | 核心货币；补刀后飘出的魂球可被正反补 |
| `deny` / `deny orbs` / `secure souls` | 反补 / 反补魂球 / 稳拿魂球 | 打掉敌方白球、抢吃己方绿球 |
| `unsecured souls` / `loose souls` / `bag` | 未稳获魂 / 掉落魂袋 | 野怪、金箱获取的魂量，死后会掉成袋子 |
| `Boon` / `Boons` / `Ability Point` (`AP`) | 恩惠（魂量等级成长） / 技能点 | 总魂量达标自动升级基础属性与发技能点 |
| `Flex Slot` / `Flex Slots` | 弹性槽位 | 额外通用装备格，需靠拆塔/拿目标解锁 |
| `Heavy Melee` (`hm`) / `Light Melee` (`lm`) | 重近战（蓄力拳） / 轻近战（轻拳） | `Q` 长按与短按 |
| `Parry` / `parried` / `bait parry` / `parry down` | 招架（振刀）/ 被振刀 / 骗振刀 / 振刀在冷却 | `F` 键格挡近战使攻击者进入长时间硬直易伤 |
| `HMC` (`Heavy Melee Cancel`) / `melee cancel` | 重近战取消 | 蓄力重拳突进瞬间用主动装（如 `Fleetfoot`）取消动作实现超远位移 |
| `Dash-Jump` (`djump`) / `Wall Jump` (`wall bounce`) | 冲刺跳 / 蹬墙跳 | 消耗 2 格精力的组合跳跃与无消耗蹬墙跳 |
| `Corner Boost` / `Mantle Glide` / `Mantle Slide` | 蹭墙加速 / 攀爬滑行 | 高阶身法跳台词 |
| `Infinite Slide` / `stair slide` / `slope slide` | 无限滑铲 / 下坡滑铲 | 斜坡下蹲滑射不消耗子弹技巧 |
| `Gun Build` / `Orange Build` / `M1 Build` | 枪械流 / 橙装流 / 左键平A流 | 偏向武器伤害的出装方向 |
| `Spirit Build` / `Purple Build` | 元灵流 / 紫装流 / 技能流 | 偏向元灵伤害的出装方向 |
| `Green Build` / `Tank Build` / `Melee Build` | 绿装流 / 肉装流 / 拳击流 | 偏向生存或近战重拳的出装方向 |
| `One Shot` / `One Tap` / `Low` | 秒杀 / 一枪死 / 残血 | 报点集火常用词 |
| `Out of Stamina` / `No Stam` / `No Stamina` | 没耐力了 / 没精力了 | 提示队友或追击没位移的敌人 |
| `Break LOS` (`line of sight`) | 卡视线 / 断视野 | 躲避凯尔文大招、七号大招、狙击等 |

---

## 四、世界观与背景设定专有名词（Lore, Factions & Worldbuilding）

用于翻译英雄台词彩蛋、背景传记、官方更新日志或社区剧情讨论：

| 英文专有名词 | 推荐中文译名 | 设定背景说明 |
| :--- | :--- | :--- |
| **The Cursed Apple** | 诅咒之苹（纽约） | 1949 年平行架空纽约市，由现实绰号 *The Big Apple* 演变而来 |
| **The First Maelstrom** / **The Maelstrom** | 初次大漩涡 / 大漩涡 | 数十年前打通人类世界与诸多灵界、使超自然生物与魔法公开现世的重大灾难 |
| **Astral Gate** / **Astral Gates** | 星界之门 | 连接地球与异界的裂隙传送门；纽约街头战正是为了借助星界之门召来守护神 |
| **The Amber Hand** | 琥珀之手 | 游戏内两大对立召唤阵营之一，供奉“隐秘之王” |
| **The Sapphire Flame** | 苍蓝之焰 | 游戏内两大对立召唤阵营之一，供奉“太初之母” |
| **The Hidden King** | 隐秘之王 | 琥珀之手阵营对应的古老守护神实体 |
| **The Archmother** | 太初之母 | 苍蓝之焰阵营对应的古老守护神实体 |
| **Ixia** / **Ixian** / **Ixians** | 伊克西亚 / 伊克西亚人 | 炽焱 (`Infernus`) 与亚伯兰 (`Abrams`) 等恶魔族裔所属的炽热异界 |
| **OSIC** (*Occult Security and Investigation Commission*) | 神秘安全与调查委员会（OSIC） | 美国联邦超自然安全与调查执法机构 |
| **Sandman** / **Sandmen** | 睡魔特工 | OSIC 麾下负责梦境潜入与目标清除的特工部门（岚梦 `Haze` 所属） |
| **The Curators** | 馆长（收容特工） | OSIC 负责收容危险诅咒遗物的专门机构 |
| **The Baxter Society** | 巴克斯特学会 | 历史悠久的职业怪兽猎人行会（希弗 `Shiv`、灰爪 `Grey Talon` 等相关） |
| **Fairfax Industries** | 费尔法克斯工业 | 垄断军工与灵魂能量科技的巨型企业（口袋 `Pocket` 的家族财阀） |
| **Blackmore Academy** | 布莱克莫尔学院 | 培养秘术师与死灵法师的知名神秘学学府（格瑞墓 `Graves` 就读院校） |
| **The Baroness Hotel** | 男爵夫人酒店 | 门侍 (`The Doorman`) 服务的超自然星界酒店 |
| **The Troubadour** | 吟游诗人号 | 海底沉船事件核心船只，与海魇 (`Fathom`) 的诞生有关 |
| **Oathkeeper** | 誓约守护者 | 寄生于盖斯特夫人 (`Lady Geist`) 断臂、以其生命为代价赐予力量的恶魔 |
| **The Seventh Moon** | 第七月组 | 大和 (`Yamato`) 所属的日本超自然黑道组织 |
| **Onyx** / **Djinn** | 玛瑙 / 巨灵 | 蜃景 (`Mirage`) 与其故乡秘术契约相关的称谓 |
| **Shroud** | 幽幕 | 幽冥与生界之间的屏障设定词汇 |

---

## 五、可直接合入 `scripts/term_extensions.py` 的参考片段

> **机制提醒（参见 [build_terms.py](../scripts/build_terms.py)）**：
> - `LEGACY_ENGLISH_NAMES` 会以 `ambiguous=False`（无歧义直替）写入词库，**不要**把日常英文单词（如 `curse`、`fervor`）放进去。
> - `AMBIGUOUS_ALIASES` 只是一个标记集合，**必须在 `ALIASES` 对应的标准名列表中也写出该词**，`build_terms.py` 才会生成 `ambiguous: true` 的词条；否则单独写在 `AMBIGUOUS_ALIASES` 中不会生效。

```python
# 1) 建议补充到 LEGACY_ENGLISH_NAMES（仅放多词或无歧义的改名前旧英文名，会直替）
LEGACY_ENGLISH_NAMES_EXTRA = {
    "Dispel Magic": ["Debuff Remover"],
    "Tankbuster": ["Improved Burst"],
    "Disarming Hex": ["Withering Whip"],
    "Mystic Expansion": ["Mystic Reach"],
    "Greater Expansion": ["Improved Reach"],
    "Compress Cooldown": ["Improved Cooldown"],
    "Bullet Resilience": ["Improved Bullet Armor"],
    "Spirit Resilience": ["Improved Spirit Armor"],
    "Silence Wave": ["Silence Glyph"],
    "Battle Vest": ["Combat Barrier"],
    "Enchanter's Emblem": ["Enchanter's Barrier"],
    "High-Velocity Rounds": ["High-Velocity Mag"],
    "Extended Magazine": ["Basic Magazine"],
    "Intensifying Magazine": ["Intensifying Clip"],
    "Hollow Point": ["Hollow Point Ward"],
    "Healing Nova": ["Health Nova"],
    "Stamina Mastery": ["Superior Stamina"],
    "Spellslinger": ["Spellslinger Headshots", "Spelldrinker"],
    "Spirit Shredder": ["Spirit Shredder Bullets", "Soul Shredder Bullets"],
}

# 2) 建议补充到 ALIASES（含直接简称，以及须同时放进 AMBIGUOUS_ALIASES 的语境词）
ALIASES_EXTRA = {
    # 英雄
    "Abrams": ["abs", "rams", "blue guy"],
    "Bebop": ["bop", "boop", "hook bot", "bomb bot"],
    "Dynamo": ["dymo", "black hole guy"],
    "Grey Talon": ["greytalon", "owl man", "bird man", "orion"],
    "Haze": ["p-haze", "sandman"],
    "Infernus": ["fern", "furnace", "flame boy"],
    "Ivy": ["gargoyle", "kudzu"],
    "Kelvin": ["kel", "ice guy", "dome guy", "frozone"],
    "Lady Geist": ["geist", "ladyg", "lady g"],
    "Lash": ["the lash", "whip guy", "jacob lash", "asshole"],
    "McGinnis": ["mcgin", "mg", "mgk", "turret lady", "engie"],
    "Mirage": ["mir", "djinn", "tornado guy", "scarab guy"],
    "Mo & Krill": ["mo&krill", "m&k", "mk", "mok", "mole guy"],
    "Paradox": ["dox", "pdox", "px", "swap lady", "time lady"],
    "Pocket": ["pock", "pkt", "frog guy", "briefcase guy", "arin fairfax"],
    "Seven": ["gigawatt", "svn", "lightning guy", "ball guy"],
    "Shiv": ["pompadour", "monkey guy", "bleed guy"],
    "Vindicta": ["dicta", "vd", "vita", "crow lady", "feet lady"],
    "Viscous": ["visc", "vis", "visk", "goo guy", "goo boy", "cube guy", "flubber"],
    "Warden": ["ward", "cop guy", "police guy"],
    "Wraith": ["wr", "card lady", "tk lady"],
    "Yamato": ["yam", "mato", "ymt", "forehead"],
    "Billy": ["billy the goat", "goat boy", "punk goat", "bat goat", "goat"],
    "Calico": ["cat lady", "leopard", "nano", "ava"],
    "Celeste": ["unicorn girl", "roller girl", "dazzler", "unicorn"],
    "Drifter": ["vamp", "creepy guy", "blood guy"],
    "Fathom": ["slork", "slark", "fish guy", "shark guy", "deep one"],
    "Graves": ["darcy", "darcy graves", "necro", "ghoul girl"],
    "Holliday": ["holli", "astro", "doc holliday", "cowgirl", "lasso girl"],
    "Mina": ["mina ha", "vampire girl", "umbrella girl"],
    "Paige": ["bookworm", "book girl", "storyteller", "dragon girl"],
    "Raven": ["umbrella guy", "russian spy", "bird guy"],
    "Rem": ["sleepy boy", "pillow guy", "lil helpers"],
    "Silver": ["lila", "lila silver"],
    "Sinclair": ["the magnificent sinclair", "bunny guy", "hex guy", "magician"],
    "The Doorman": ["door man", "door guy", "bellhop", "hotel guy", "portal guy"],
    "Trapper": ["trap guy", "pest control", "exterminator", "web guy"],
    "Venator": ["father quinn", "quinn rourke", "vatican guy", "crossbow priest", "priest"],
    "Victor": ["vic", "frankenstein", "zombie guy", "undead guy"],
    "Vyper": ["vyp", "kali", "snake", "snek", "slide girl"],
    "Wrecker": ["wreck", "junk guy", "scrap guy"],
    # 装备别名 / 简称 / 常见外号（含歧义项，歧义项见第 3 节）
    "High-Velocity Rounds": ["hvr", "hvm", "high velocity"],
    "Close Quarters": ["cq"],
    "Point Blank": ["pb"],
    "Headshot Booster": ["hsb", "hs booster"],
    "Headhunter": ["hh"],
    "Weakening Headshot": ["whs", "weakening hs"],
    "Crippling Headshot": ["chs", "cripple hs", "crippling"],
    "Hollow Point": ["hpw"],
    "Monster Rounds": ["mr"],
    "Cultist Sacrifice": ["cultist", "midas"],
    "Active Reload": ["ar"],
    "Kinetic Dash": ["kdash"],
    "Fleetfoot": ["fleetfoot boots", "fleet"],
    "Mystic Shot": ["mshot"],
    "Slowing Bullets": ["slow bullets"],
    "Opening Rounds": ["pristine emblem"],
    "Escalating Resilience": ["er"],
    "Intensifying Magazine": ["imag", "intensifying mag"],
    "Berserker": ["zerk", "berzerker"],
    "Burst Fire": ["bf"],
    "Long Range": ["lr"],
    "Hunter's Aura": ["hunters aura", "ha"],
    "Heroic Aura": ["heroic"],
    "Alchemical Fire": ["alch fire", "alch", "molly"],
    "Tesla Bullets": ["tesla", "maelstrom"],
    "Toxic Bullets": ["toxic", "bleed bullets"],
    "Capacitor": ["nullifier"],
    "Shadow Weave": ["shadowblade", "shadow blade"],
    "Ricochet": ["rico"],
    "Glass Cannon": ["gc"],
    "Armor Piercer": ["apr", "ap rounds", "armor piercing rounds"],
    "Siphon Bullets": ["siphon"],
    "Silencer": ["silence bullets"],
    "Spiritual Overflow": ["overflow"],
    "Vampiric Burst": ["vb", "vamp burst", "satanic"],
    "Healing Rite": ["rite", "salve"],
    "Healing Nova": ["nova", "mek", "mekansm"],
    "Restorative Locket": ["locket", "wand", "magic wand"],
    "Bullet Armor": ["ba", "gun armor"],
    "Bullet Resilience": ["iba", "bullet res"],
    "Spirit Armor": ["sa", "magic armor"],
    "Spirit Resilience": ["isa", "spirit res"],
    "Debuff Reducer": ["dr"],
    "Dispel Magic": ["dispeller", "remover", "cleanse"],
    "Divine Barrier": ["db"],
    "Reactive Barrier": ["rb"],
    "Return Fire": ["blademail", "blade mail", "rf"],
    "Healbane": ["hb", "antiheal", "anti-heal"],
    "Fortitude": ["fort"],
    "Majestic Leap": ["mleap", "leap"],
    "Counterspell": ["linkens", "lotus"],
    "Spellbreaker": ["magebane"],
    "Plated Armor": ["plated"],
    "Juggernaut": ["jug", "jugg"],
    "Trophy Collector": ["trophy", "tc", "mejai"],
    "Veil Walker": ["vw", "veil"],
    "Fury Trance": ["fervor", "mom"],
    "Blood Tribute": ["bt", "armlet"],
    "Phantom Strike": ["ps"],
    "Unstoppable": ["unstop"],
    "Inhibitor": ["inhib"],
    "Cheat Death": ["soul rebirth", "ga"],
    "Witchmail": ["witch mail"],
    "Boundless Spirit": ["boundless", "dcap"],
    "Extra Charge": ["ec"],
    "Rapid Recharge": ["rr"],
    "Mystic Burst": ["mburst", "mb"],
    "Tankbuster": ["ib"],
    "Greater Expansion": ["ge", "greach"],
    "Compress Cooldown": ["ic"],
    "Superior Cooldown": ["sup cd", "superior cd", "sc"],
    "Transcendent Cooldown": ["octarine"],
    "Duration Extender": ["de"],
    "Superior Duration": ["sup dur", "superior dur", "sd"],
    "Surge of Power": ["sop", "surge"],
    "Spirit Snatch": ["ssnatch", "snatch"],
    "Mystic Vulnerability": ["mv"],
    "Escalating Exposure": ["exposure"],
    "Decay": ["spirit vessel"],
    "Slowing Hex": ["shex", "slow hex", "atos"],
    "Disarming Hex": ["dhex", "disarm hex", "halberd"],
    "Silence Wave": ["swave"],
    "Focus Lens": ["fl", "orchid", "bloodthorn"],
    "Cold Front": ["cf"],
    "Arctic Blast": ["shivas", "shiva"],
    "Knockdown": ["anvil"],
    "Torment Pulse": ["radiance"],
    "Ethereal Shift": ["eshift", "e-shift", "zhonya", "zhonyas", "euls"],
    "Cursed Relic": ["curse", "hex", "sheepstick", "vyse", "scythe of vyse"],
    "Echo Shard": ["eshard", "echo"],
    "Magic Carpet": ["carpet"],
    "Refresher": ["refresher orb"],
    "Diviner's Kevlar": ["diviners", "dk", "kevlar"],
    "Vortex Web": ["vortex"],
}

# 3) 必须同时加入 AMBIGUOUS_ALIASES（仅语境触发或喂给 LLM 上下文，禁止裸替换）
AMBIGUOUS_ALIASES_EXTRA = {
    # 英雄歧义短词
    "abs", "rams", "orion", "fern", "furnace", "kel", "asshole", "mg", "mgk",
    "engie", "mir", "djinn", "mk", "px", "pkt", "svn", "vd", "vita", "vis",
    "ward", "wr", "yam", "ymt", "forehead", "goat", "ava", "nano", "unicorn",
    "slark", "darcy", "magician", "priest", "vic", "kali", "snake", "snek",
    # 装备歧义简称 / MOBA 类比 / 常见英语词
    "cq", "pb", "hh", "mr", "midas", "ar", "fleet", "er", "bf", "lr", "ha",
    "heroic", "alch", "molly", "maelstrom", "toxic", "nullifier", "siphon",
    "overflow", "vb", "rite", "salve", "nova", "mek", "mekansm", "locket",
    "wand", "magic wand", "ba", "sa", "dr", "remover", "cleanse", "db", "rb",
    "rf", "hb", "fort", "leap", "linkens", "lotus", "plated", "jug", "jugg",
    "trophy", "tc", "vw", "veil", "fervor", "mom", "bt", "armlet", "ps",
    "unstop", "inhib", "ga", "boundless", "dcap", "ec", "rr", "mb", "ib", "ge",
    "ic", "sc", "octarine", "de", "sd", "surge", "snatch", "mv", "exposure",
    "spirit vessel", "atos", "halberd", "fl", "orchid", "cf", "shiva",
    "anvil", "radiance", "curse", "hex", "echo", "carpet", "dk", "kevlar",
    "vortex",
}

# 4) 建议补充到 PHRASES（高频局内黑话与短语；单字或常见词须加入 AMBIGUOUS_PHRASES）
PHRASES_EXTRA = {
    "sinner's sacrifice": "罪人献祭",
    "sinners sacrifice": "罪人献祭",
    "slot machine": "罪人献祭（老虎机）",
    "golden statue": "金雕像增益",
    "statue buff": "金雕像增益",
    "bridge buff": "桥上增益符",
    "bridge buffs": "桥上增益符",
    "shoot off zip": "从滑索上打下来",
    "zip knockdown": "从滑索上打下来",
    "corner boost": "蹭墙加速",
    "mantle glide": "攀爬滑行",
    "infinite slide": "无限滑铲",
    "rosh": "中立首领（肉山）",
    "do rosh": "打中立首领",
    "baron": "中立首领（大龙）",
    "the cursed apple": "诅咒之苹（纽约）",
    "amber hand": "琥珀之手",
    "sapphire flame": "苍蓝之焰",
    "hidden king": "隐秘之王",
    "archmother": "太初之母",
    "baxter society": "巴克斯特学会",
    "fairfax industries": "费尔法克斯工业",
    "blackmore academy": "布莱克莫尔学院",
}

# 5) 建议补充到 AMBIGUOUS_PHRASES
AMBIGUOUS_PHRASES_EXTRA = {
    "baron",
}

# 6) 建议补充到 AMBIGUOUS_HINTS（多义双字母缩写，供高质量翻译上下文参考）
AMBIGUOUS_HINTS_EXTRA = {
    "ap": "可能表示穿甲弹（Armor Piercer）或技能点数（Ability Point），须结合上下文",
    "tp": "可能表示传送门（Teleporter）或痛苦脉冲（Torment Pulse），须结合上下文",
    "dr": "可能表示减益缩短（Debuff Reducer）或伤害减免（Damage Reduction），须结合上下文",
    "ls": "可能表示吸血（Lifesteal）、幸运一击（Lucky Shot）或雷鸣卷轴（Lightning Scroll），须结合上下文",
    "sb": "可能表示减速弹（Slowing Bullets）或破咒护符（Spellbreaker），须结合上下文",
    "mr": "可能表示猎怪弹（Monster Rounds）、秘术余波（Mystic Reverb）或魔抗，须结合上下文",
    "db": "可能表示神圣屏障（Divine Barrier）或减益效果（Debuff），须结合上下文",
    "tc": "可能表示超凡冷却（Transcendent Cooldown）或寻猎季节（Trophy Collector），须结合上下文",
}
```
