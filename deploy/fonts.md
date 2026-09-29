# 生僻字字体：从哪来、放哪、怎么验证生效

> 本文对应 ROADMAP 第 15 条（技术债 15）的验收标准：
> "`deploy/` 里给出『字体目录从哪来、放哪、怎么验证生效』的一页说明"。
>
> 相关代码（**只读参考，本文不改代码**）：`src/Xjgl.Reporting/XjglReportingModule.cs`
> （`PrintFontRegistry`）、`src/Xjgl.Reporting/StudentCardPdfRenderer.cs`、
> `src/Xjgl.Application/Students/StudentPrintService.cs`。
> 相关文档：`docs/learning/25-生僻字全链路-字体检索与打印.md`、
> `docs/articles/03-生僻字全链路-不出现方块.md`。

**一页速查**

| 问题 | 答案 |
|---|---|
| 用什么字体 | [遍黑体项目（Plangothic Project）](https://github.com/Fitzgerald-Porthmouth-Koenigsegg/Plangothic_Project)，取 **Static 发行版**里的 `PlangothicP1-Regular.ttf` / `PlangothicP2-Regular.ttf` |
| 许可证 | 字体文件是 **SIL Open Font License 1.1**（保留名称 `Plangothic` / `遍黑`）；仓库里的构建代码是 MIT |
| 放哪（compose） | 宿主机 `deploy/fonts/rare/`（或 `RARE_FONT_DIR` 指定的目录）→ 容器 `/app/fonts/rare`（**只读**） |
| 放哪（K8s） | 节点 `/opt/xjgl/fonts/rare` → 容器 `/app/fonts/rare`（见 `k8s/api-deployment.yaml`） |
| 配哪个键 | `Xjgl:Print:RareCharacterFontDirectory`（目录）+ `Xjgl:Print:RareCharacterFontFamily`（**家族名，必须与文件内部一致**） |
| 基字体 | `Xjgl:Print:DefaultFontFamily`（compose 里用 `PRINT_FONT_FAMILY` 注入，容器内默认 `Noto Sans CJK SC`） |
| 没放会怎样 | **不会报错**，只是生僻字打不出来：打印时**明确失败**（不是出方块），日志与接口提示都会指向"字体目录" |
| 怎么验证 | 见第 4 节四步，其中**第三步（空目录反向对照）是唯一能证明"字形真的来自这个目录"的一步** |

---

## 1. 字体文件从哪来

### 1.1 上游项目

**遍黑体项目（Plangothic Project）** —— <https://github.com/Fitzgerald-Porthmouth-Koenigsegg/Plangothic_Project>

- 上游自述：基于思源黑体、以中国大陆字形为标准，对中日韩越统一表意文字**扩展区**做字形补充；
  目前覆盖**扩展 B 区至扩展 J 区**。
- 取 **Static 发行版**（[releases/latest](https://github.com/Fitzgerald-Porthmouth-Koenigsegg/Plangothic_Project/releases/latest)）：
  它里面是 `PlangothicP1-Regular.ttf` 与 `PlangothicP2-Regular.ttf` 两个独立 TTF（外加一个 TTC）。
  **请用两个独立 TTF**，不要用 TTC：上游 README 明确说 TTC 在非 Windows 平台上可能只能显示其中一个字体，
  而 PDF 渲染是 Linux 容器里跑的。
- ⚠ **不要把 Web 发行版（`*.woff2`）放进这个目录**。浏览器用的 woff2 与 QuestPDF 需要的
  TTF/OTF/TTC 不是一回事：`PrintFontRegistry` 只认 `*.ttf` / `*.otf` / `*.ttc`
  （见 `XjglReportingModule.cs` 的 `HasFontFile`），woff2 放进去等于没放。

### 1.2 许可证（部署前请自己过一眼）

上游 README 与 `LICENSE-OFL.txt` 的口径：

- **字体文件：SIL Open Font License 1.1** —— 可免费商用、可嵌入软件/设备、可再分发；
- **不可以**把字体文件**单独售卖**；二次修改的版本不能用保留名称 `Plangothic` / `遍黑`，
  且必须以 OFL 发布（原样分发/格式转换不触发这一条 —— 本项目就是原样分发）；
- 仓库里的构建脚本与配置是 **MIT**；
- 上游自己加了一句提醒：如果所在单位的法务不认可 OFL 的效力、要求传统"授权证明"，
  上游建议**放弃使用本字体**，改用能开授权证明的字体或购买商业授权。

> 本仓库里关于许可证的口径有一处**不一致**，部署前请注意：
> `docs/学籍管理系统-总体方案.md` 第 340 行把上游写成"**落霞孤鹜**"，
> 而 `deploy/README.md` 第 9 节的表格写的是"Plangothic 遵循其自身授权，请自行确认"。
> 以上游自述为准：项目名是**遍黑体（Plangothic Project）**，
> "落霞孤鹜"更像是另一款字体（霞鹜文楷）的名字。本文没有去改那两份文档
> （它们各自有上下文），但这条差异应该被统一掉。

### 1.3 前端还需要的字体（与本文的 PDF 字体是**两份文件**）

浏览器里显示生僻字走的是另一条链路：`web/apps/pc` 与 `web/apps/h5` 的
`src/styles/index.css` 都用 `@font-face` 引了 `/fonts/PlangothicP1-Regular.woff2` 与
`PlangothicP2-Regular.woff2`（对应上游的 **Web 发行版**）。
这两份 woff2 需要放进各应用自己的 `public/fonts/`（例如 `web/apps/pc/public/fonts/`）。
它们同样**不进仓库**，缺失时浏览器回落到系统字体 —— 生僻字显示为方块，
但**打印链路不受影响**（打印只认服务器上那两份 TTF）。
本仓库当前 `web/apps/pc/public/` 目录并不存在（也就是说前端字体分片现在就没放），
这一条不影响部署，只是"页面上看到方块"时要想到它。

---

## 2. 字体文件放在哪

### 2.1 compose（`deploy/docker-compose.prod.yml`）

```yaml
# 宿主机目录 → 容器只读
- ${RARE_FONT_DIR:-./fonts/rare}:/app/fonts/rare:ro
# 容器内的配置（写死在 compose 里）
Xjgl__Print__RareCharacterFontDirectory: /app/fonts/rare
```

对应到 `deploy/README.md` 第 2 章的步骤：

```bash
mkdir -p /opt/xjgl/deploy/fonts/rare
cp PlangothicP1-Regular.ttf PlangothicP2-Regular.ttf /opt/xjgl/deploy/fonts/rare/
# .env 里（默认就是 ./fonts/rare，用绝对路径更不容易出错）
RARE_FONT_DIR=/opt/xjgl/deploy/fonts/rare
```

### 2.2 Kubernetes（`deploy/k8s/`）

节点目录 → 容器只读（`api-deployment.yaml` 里的 `rare-fonts` 卷）：

```bash
sudo mkdir -p /opt/xjgl/fonts/rare
sudo cp PlangothicP1-Regular.ttf PlangothicP2-Regular.ttf /opt/xjgl/fonts/rare/
```

hostPath 用的是 `type: Directory`：**目录不存在时 Pod 直接起不来**。
这是刻意的（与"挂一个空目录会让字体问题变成安静的、事后才发现的问题"相比，
起不来更容易定位）；但要注意**每个节点**都要放同一份文件，
或者改用 RWX 共享卷（`deploy/k8s/README.md` 第 8 节第 5 条）。
根目录下的 `deploy/k8s/README.md` 第 3.1 节也写了这一步。

### 2.3 三个容易踩的细节

1. **必须是目录的顶层**：`HasFontFile` 只看 `SearchOption.TopDirectoryOnly`
   —— 把字体放进 `rare/plangothic/` 子目录等于没放（它会认为目录里没有字体文件）；
2. **文件名随意，家族名才是关键**（见第 3.2 节）；
3. **只读挂载**：字体只需要被读。compose 里带了 `:ro`，K8s 清单里是 `readOnly: true`。

---

## 3. 要配哪些键

### 3.1 `Xjgl:Print:RareCharacterFontDirectory`（生僻字目录）

- compose / K8s 都已经配好：`/app/fonts/rare`；
- Windows 直接跑（`dotnet run`）时它默认是空的 → 目录不存在或为空时
  `PrintFontRegistry` 会**静默把备用字体族置为 null**（不报错、不写日志），
  于是生僻字只能靠系统字体，缺字形时打印失败。

### 3.2 `Xjgl:Print:RareCharacterFontFamily`（家族名，**最容易配错的一项**）

默认值是 `Plangothic P1`（`appsettings.json`）。它必须与 **TTF 文件内部的家族名**完全一致。
对不上的后果不是报错，而是：QuestPDF 在 `ThrowOnMissingFontFamilies = false` 下
**静默换一个字体族**，然后在"画字形"那一步失败 —— 报错和你"根本没挂字体"时一模一样，
排查方向会被带偏。

所以放完文件先核对家族名（api 镜像里装了 fontconfig，`fc-scan` 属于同一个包；
你的发行版若把 `fc-scan` 单独拆包，就用 `fc-list` 兜底）：

```bash
docker compose -f deploy/docker-compose.prod.yml exec api \
  fc-scan --format '%{family}\n' /app/fonts/rare/PlangothicP1-Regular.ttf
# 期望输出：Plangothic P1      ← 与 Xjgl:Print:RareCharacterFontFamily 一模一样

docker compose -f deploy/docker-compose.prod.yml exec api fc-list | grep -i plangothic
# 兜底写法：应该能看到 /app/fonts/rare 下的两个文件
```

> ⚠ **诚实说明**：本文**没有**下载字体文件去核对它内部的家族名到底是
> `Plangothic P1` 还是别的写法（仓库里不分发字体、也不该为了写文档去下载几十 MB 的字体）。
> 仓库里三处地方都假设是 `Plangothic P1` / `Plangothic P2`
> （`appsettings.json`、`web/apps/pc/src/styles/index.css`、`web/apps/h5/src/styles/index.css`），
> 但**真正的事实来源是字体文件本身** —— 请以 `fc-scan` 的输出为准，
> 不一致时改配置（不是改字体文件）。

### 3.3 基字体 `Xjgl:Print:DefaultFontFamily`（常用汉字）

- 容器镜像里装了 `fonts-noto-cjk`（`api.Dockerfile`），compose 把它注入为
  `Xjgl__Print__DefaultFontFamily: ${PRINT_FONT_FAMILY:-Noto Sans CJK SC}`；
- **`XJGL_PRINT_FONT` 这个环境变量应用进程根本不读**：只有测试工程读它
  （`test/Xjgl.TestBase/XjglTestConfiguration.cs`、`test/Xjgl.Domain.Tests/Printing/StudentPdfRendererTests.cs`，
  CI 里两个都设了）。部署时只设 `XJGL_PRINT_FONT` 是**不会生效**的 ——
  这是 `docs/learning/25` 里那段示例代码与当前实现的差异，以代码为准。
- ⚠ **不要轻易把 `Xjgl:Print:UseSystemFonts` 设成 `false`**：
  `appsettings.json` 的注释里写着"容器化部署建议设为 false"，但那是有前提的 ——
  设成 false 之后 QuestPDF **只认**随应用部署的字体文件（`FontDiscoveryPaths`，
  目前只有生僻字目录），而镜像里的 Noto 是**系统字体**，会被一起排除，
  结果是**常用汉字也报"字体缺少字形"**，比生僻字打不出来严重得多。
  真要用 false，必须把基字体也放进一个发现目录（当前代码没有为它留配置键）。

---

## 4. 怎么验证生效（四步，第 3 步是决定性的）

前置：库里得有一个**姓名含生僻字**的学生。演示种子数据里有
`李䶮`（U+4DAE，扩展 A）与 `周𠮷`（U+20BB7，扩展 B，UTF-16 代理对），
见 `src/Xjgl.DbMigrator/DemoData/DemoDataSeedContributor.cs`；
自己的库没有就用导入/接口建一个（姓名校验只收规范汉字，`𠮷` 是合法的）。

> ⚠ 先排除一个**假证据**：学籍卡页脚上的「**注：姓名含生僻字，请核对原件**」
> **不能**用来说明字体生效了 —— 它只取决于姓名里有没有扩展区码点
> （`StudentCardPdfRenderer` 里的 `model.HasRareCharacter`），与字体加载无关。

### 第 1 步：文件真的挂进来了吗

```bash
docker compose -f deploy/docker-compose.prod.yml exec api ls -l /app/fonts/rare
# K8s： kubectl -n xjgl exec deploy/api -- ls -l /app/fonts/rare
```

- **期望**：至少一个非 0 字节的 `.ttf`/`.otf`/`.ttc`，**就在这一层**；
- 空目录：不报错，但备用字体族会被置 null → 第 3 步一定失败；
- 只有 woff2：等于空目录（`HasFontFile` 不认）。

### 第 2 步：家族名对得上吗

见第 3.2 节的 `fc-scan` / `fc-list`。
**期望**：输出的家族名与 `Xjgl:Print:RareCharacterFontFamily`（默认 `Plangothic P1`）完全一致。

### 第 3 步（决定性）：空目录反向对照

只做前两步还不够 —— **它们证明不了"这个字形是从这个目录里来的"**。
真正能区分"字体生效"与"回落到了系统字体"的做法是做一次**反向对照**：

1. 先正常打印一次那个含生僻字学生的学籍卡（界面上：学生列表 → 详情 → 打印学籍卡），
   把 PDF 存下来，并记下 api 日志（`docker compose ... logs api` 或
   `kubectl -n xjgl logs deploy/api`）里**没有**「渲染 PDF 失败」；
2. 把生僻字目录换成一个**空目录**并重启 api（只改这一项，其它不动）：
   ```
   # compose：.env 里改成 RARE_FONT_DIR=/tmp/empty-fonts（目录建好且有挂载），
   #          或直接在 compose 的环境变量里覆盖：
   #          Xjgl__Print__RareCharacterFontDirectory: /tmp/empty-fonts
   # K8s：kubectl -n xjgl set env deployment/api Xjgl__Print__RareCharacterFontDirectory=/tmp/empty-fonts
   ```
   再打印同一个学生：
   - **必须失败**（接口 5xx），日志出现「渲染 PDF 失败」，
     并且接口把运维提示原样抛出来：
     > 生成 PDF 失败：服务器缺少可用的中文字体。请在服务器上安装中文字体，
     > 或把 `Xjgl:Print:RareCharacterFontDirectory` 指向随应用部署的字体目录。

     （提示语来自 `Xjgl.Application/Students/StudentPrintService.cs`；
     这**就是**本节要的"能区分回落"的信号 —— 因为渲染器刻意开着
     `ThrowOnMissingTextGlyphs = true`，画不出字形**必须报错**，不许出方块。）
   - **如果它还是成功**：说明这个字的字形来自系统字体或别的路径，
     这次对照证明不了生僻字字体在起作用。换一个更偏的字再试
     （例如扩展 G 的 `𰻞` U+30EDE），或换一个库里确实是"只靠扩展区字体才能画出来"的姓名。
3. 恢复目录、重启，再打印一次：必须成功。

**这一步的结论只有两种，都很明确**：
空目录时失败 + 恢复后成功 = 字体目录真的在起作用；空目录时也成功 = 你还没证明它起作用。

### 第 4 步：字体真的**内嵌**进 PDF 了吗（换台电脑打开也得是对的）

PDF 是自描述的，但**不内嵌字体**时阅读器会拿本机字体去凑 —— 换一台没装遍黑体的电脑就变回方块。

```bash
pdffonts 学籍卡.pdf
# 期望：name 列里能看到那个生僻字字体，emb 列是 "yes"
# 没有 pdffonts（poppler-utils）时的替代：在 PDF 里找字体描述符
grep -c FontFile2 学籍卡.pdf     # 期望 > 0
```

（`pdffonts` 不在 api 镜像里，在**宿主机**上对下载下来的 PDF 跑。这条与
`docs/articles/03` 第六节的自查清单第 7 条是同一件事。）

---

## 5. 没装 / 装错时会看到什么（把"症状 → 原因"对上）

| 症状 | 原因 | 怎么办 |
|---|---|---|
| 打印接口报「**服务器缺少可用的中文字体**」 | 字体链里谁都画不出这个码点（`ThrowOnMissingTextGlyphs` 生效） | 按第 4 步查：目录空 / 只有 woff2 / 家族名不匹配 / 文件在子目录 |
| 打印接口报同样的错，但**常用汉字**也打不出来 | 有人把 `UseSystemFonts` 设成了 `false`，把镜像里的 Noto 一起排除了 | 改回 `true`，或把基字体也放进发现目录（见第 3.3 节） |
| 页面上生僻字是方块（打印正常） | 前端 woff2 分片没放（第 1.3 节），与服务器字体无关 | 放 `public/fonts/PlangothicP*.woff2`，或接受"页面方块、打印正确" |
| 卡片上有「注：姓名含生僻字，请核对原件」 | 这是**功能性提示**，不是错误 | 正常现象；但要按第 4 步确认字形真的画出来了 |

## 6. 诚实说明：**目前没有**"启动日志 / 健康检查能看出字体状态"这回事

`src/Xjgl.Reporting/XjglReportingModule.cs` 里 `RareCharacterFontAvailable`
的文档注释写的是"用于启动日志与健康检查"，
但**实际代码里没有任何地方读它**（全文检索确认：除 `PrintFontRegistry` 自身与单元测试
`test/Xjgl.Domain.Tests/Printing/StudentPdfRendererTests.cs` 之外没有调用点），
`/health` 也只看数据库。

也就是说：

- **不存在**一条"启动时打了 `[字体] 生僻字目录已加载`"的日志可以看；
  目录为空/家族名不匹配都**不会**在启动日志里留下任何痕迹；
- 因此"验证生效"只能靠第 4 节那四条（`ls` → `fc-scan` → 空目录反向对照 → `pdffonts`）。

如果以后想让它更好验证，最小改动是在启动时把
`RareCharacterFontAvailable` / `RareCharacterFontFamily` 打一行 Information 日志
（或加进 `/health` 的 JSON）—— 那属于代码改动，本文按"只写部署文档"的边界没有做，
留在这里作为一个明确、可执行的改进建议。
