# 把 FileFlipper 免费上架 Mac App Store

> English summary at the bottom.

App Store Connect 里的 App 记录已经建好了：

| 项目 | 值 |
|---|---|
| 名称 | FileFlipper |
| 套装 ID（Bundle ID） | `com.aimeesun.fileflipper` |
| SKU | `fileflipper-mac-001` |
| Apple ID | 6818597213 |
| 主要语言 | 英语（美国） |

代码里的名称和套装 ID 已经和上表一致，**不要再改**。

整个流程分三段：**A. 在 Xcode 里打包上传** → **B. 在 App Store Connect 里填资料** → **C. 提交审核**。

---

## A. 在 Xcode 里打包上传（约 15 分钟）

### A1. 登录开发者账号（只需一次）
1. 打开 **Xcode** → 屏幕左上角菜单 **Xcode → Settings…（设置）** → **Accounts（账户）**。
2. 点左下角 **＋** → **Apple Account** → 用你的开发者账号登录。

### A2. 打开项目并选择签名团队
1. 双击 `FileFlipper.xcodeproj` 打开项目。
2. 点左侧最上面蓝色的 **FileFlipper** 项目图标 → 中间选 **TARGETS** 下的 **FileFlipper** → 上方标签 **Signing & Capabilities**。
3. **Team** 选你的开发者账号（显示你的名字），保持 **Automatically manage signing** 勾选。
4. 确认 **Bundle Identifier** 是 `com.aimeesun.fileflipper`。
5. 下面应该能看到 **App Sandbox**，以及 User Selected File 设成 Read/Write。如果出现红色错误，把错误文字发给我。

### A3. 本地测试一遍
1. 按 **⌘R** 运行。右上角菜单栏出现 ◎ 图标。
2. 在 Finder 里拖动文件，按住 Shift，拖到格式上松手，确认能转换。
3. 测完后点 ◎ → **Quit FileFlipper** 退出。

### A4. 打包（Archive）
1. Xcode 窗口最上方中间，运行目标选 **My Mac**（或 Any Mac）。
2. 菜单 **Product → Archive**。等待 1–3 分钟，会自动弹出 **Organizer** 窗口。

### A5. 上传
1. 在 Organizer 里选中刚才的版本 → 右侧点 **Distribute App**。
2. 选 **App Store Connect** → **Distribute**（或 Upload），一路点 **Next**，签名选「自动管理」。
3. 看到 **Uploaded** 就是成功了。
4. 等 **5–30 分钟**，苹果处理完后会给你发邮件，构建版本会出现在 App Store Connect 里。

> 以后每次重新上传，`project.yml` 里的 `CURRENT_PROJECT_VERSION`（构建号）都必须比上一次大，否则会被拒收。告诉我一声，我帮你改。

---

## B. 在 App Store Connect 里填资料

登录 <https://appstoreconnect.apple.com> → **App** → **FileFlipper**。

### B1. App 信息
| 栏目 | 填写 |
|---|---|
| 副标题 | `Markdown for AI, in Finder`（或保留原来的 `Convert files right in Finder`） |
| 类别 → 主要 | **工具（Utilities）** |
| 类别 → 次要 | **效率（Productivity）** |
| 内容版权 | 选「不包含、显示或访问第三方内容」 |

填完点右上角 **保存**。

### B2. 价格与销售范围
1. 左侧 **价格与销售范围** → 价格选 **免费（USD 0.00）**。
2. **销售范围（国家/地区）**：⚠️ **中国大陆**需要提供 **ICP 备案号**才能上架。没有备案的话，先**取消勾选「中国大陆」**，其他国家和地区保持全选。（备案好以后可以再加上。）

### B3. App 隐私
1. 左侧 **App 隐私** → **隐私政策网址** 填：
   `https://github.com/Aimee51819/FileFlipper/blob/main/PRIVACY.md`
2. **数据收集** → 点「开始」→ 选 **「否，我们不会从此 App 收集数据」** → 发布。

### B4. 年龄分级
所有问题都选 **「无」/「否」**，结果是 **4+**。

### B5. 版本页面（左侧「macOS App」下的 1.0 / 准备提交）
| 栏目 | 填写 |
|---|---|
| 截图 | 上传 `docs/app-store-screenshots/` 里的 5 张图（尺寸 2880×1800，已经做好）。建议把 `5-markdown.png` 放在**第 1 张**，突出「一键转 Markdown」 |
| 宣传文本 | 见下方「文案」 |
| 描述 | 见下方「文案」 |
| 关键词 | `convert,converter,file,finder,image,pdf,heic,jpg,png,webp,docx,pptx,xlsx,markdown,compress,crop,ocr` |
| 技术支持网址 | `https://github.com/Aimee51819/FileFlipper/issues` |
| 营销网址（可不填） | `https://github.com/Aimee51819/FileFlipper` |
| 版权 | `2026 你的名字`（例如 `2026 Aimee Sun`） |
| 构建版本 | 点「添加构建版本」，选 A5 上传的那个 |
| App 审核信息 → 登录信息 | 取消勾选「需要登录」 |
| App 审核信息 → 联系信息 | 填你的名字、电话、邮箱 |
| App 审核信息 → 备注 | 见下方「给审核员的备注」 |
| 版本发布 | 选「手动发布」或「自动发布」都行（自动 = 审核通过就上架） |

### B6. 欧盟「交易商」声明（如果系统要求）
如果提示要声明 **DSA 交易商身份**：个人开发免费 App，选 **「我不是交易商」** 即可。

---

## C. 提交审核

1. 版本页面右上角点 **添加以供审核** → **提交以供审核**。
2. 状态会变成 **等待审核** → **正在审核** → **可供销售**。一般 **1–3 天**，苹果会发邮件通知。
3. 如果被拒，邮件和 App Store Connect 里会写原因，**把原文发给我**，我帮你改代码或写回复。

---

## 文案（直接复制）

**宣传文本 / Promotional Text**（170 字符以内）
```
One-click Markdown for AI: turn Word, PDF, PowerPoint and Excel into clean Markdown with fewer tokens. Plus quick conversions right in Finder.
```

**描述 / Description**
```
FileFlipper is a free, open-source file converter that works inside Finder.

ONE-CLICK MARKDOWN FOR AI
Turn Word, PDF, PowerPoint and Excel files into clean Markdown in seconds, ready to paste into your AI assistant. Markdown keeps headings, lists and tables while dropping layout clutter, so it usually takes fewer tokens than uploading the original file, and the AI understands the structure better. Scanned PDFs are read with on-device OCR.

Pick up a file, press Shift, and a curved row of icon buttons appears above your cursor. Flick toward JPG, PDF, MP4 or any other format, let go, and a new copy appears in the same folder. The original stays untouched. Press Option + Shift for quick edits instead.

WHAT YOU CAN DO
• Turn photos into JPG, PNG, HEIC, WEBP, TIFF, GIF, BMP or PDF
• Turn PDFs into images, text, Markdown, Word or RTF. Scanned pages are read with on-device OCR (Chinese, English and more)
• Switch documents between DOCX, PDF, RTF, TXT, HTML, ODT and DOC
• Turn PowerPoint slides and Excel sheets into PDF
• Save any document, PDF, slide deck or spreadsheet as Markdown
• Turn videos into MP4, MOV, animated GIF or audio
• Save audio as M4A, WAV, AIFF or CAF

QUICK EDITS
• Photos: crop with preset ratios, shrink, cut out the background, remove location and camera data, resize, rotate, flip, black and white, combine into a PDF
• PDFs: shrink, split into pages, pull out the text, rotate, combine
• Videos: shrink, 720p, mute, pull out the audio, grab a frame
• Audio: shrink, mono

FREE AND OPEN
No ads, no account, no limits, no in-app purchases. The full source code is on GitHub under the MIT License. Nothing is uploaded or tracked. Every file is processed on your Mac with Apple's built-in frameworks.

FileFlipper sits in the menu bar. Use its menu to pause it or start it at login.
```

**给审核员的备注 / App Review Notes**
```
FileFlipper is a menu bar app with no Dock icon. After launch, its icon (a circle) appears in the menu bar; the menu contains "How to Use…".

To test:
1. Open Finder and start dragging any image or PDF. While still holding the mouse button, press and hold the Shift key.
2. Round icon buttons appear in an arc above the pointer, one per output format. Move toward a format (e.g. JPG) and release.
3. The converted copy is saved next to the original, and a confirmation appears at the bottom of the screen.
4. Hold Option + Shift while dragging to see tools (Crop opens a small crop window).

The app is sandboxed. The first time it saves a file, it shows an Open panel asking for access to the folder; choose the Home folder to grant access once. No account, login or network connection is needed.
```

---

## 中文版（简体中文）App Store 页面

App 本身已经支持简体中文：用户的 Mac 系统语言是中文时，菜单、按钮、提示都会自动显示中文。App Store 页面也可以单独设置中文版：

1. App Store Connect → FileFlipper → **App 信息** → 右上角语言下拉菜单（现在是「英语（美国）」）→ **添加语言** → 选 **简体中文**。
2. 切换到「简体中文」，填写下面这些（英文版不受影响）：

| 栏目 | 填写 |
|---|---|
| 名称 | `FileFlipper` |
| 副标题 | `一键转Markdown·喂AI更省` |
| 宣传文本 | 见下方 |
| 描述 | 见下方 |
| 关键词 | `格式转换,文件转换,转换器,图片转换,PDF转换,HEIC转JPG,Markdown,转MD,Word转PDF,PPT转PDF,Excel转PDF,压缩,裁剪,抠图,OCR,文字识别,AI` |
| 截图 | 上传 `docs/app-store-screenshots/zh-Hans/` 里的 5 张中文截图（建议 `5-markdown.png` 放第 1 张） |

> ⚠️ 上架**中国大陆区**需要 **ICP 备案号**（见 B2）。没有备案的话，中文页面仍然会给台湾、香港、新加坡、马来西亚等地区的中文用户看到。

**宣传文本**
```
一键转 Markdown，喂给 AI 更省 token：Word、PDF、PPT、Excel 秒变干净的 Markdown。还能在 Finder 里直接转格式、裁剪、压缩、抠图。
```

**描述**
```
FileFlipper 是一个免费、开源的 Mac 文件转换工具，直接在 Finder 里使用。

拖动文件时按住 Shift，鼠标上方会弹出一排带图标的圆形按钮。往想要的格式方向一拖、松开鼠标，新文件就保存在原文件旁边，原文件不会被改动。拖动时按住 Option + Shift，则显示这类文件能用的快捷工具。

一键转 MARKDOWN，喂给 AI 更省 TOKEN
把 Word、PDF、PPT、Excel 几秒钟转成干净的 Markdown，直接粘贴给 AI 助手。Markdown 保留标题、列表和表格，去掉排版上的冗余，通常比直接上传原文件占用更少的 token，AI 也更容易理解文档结构。扫描版 PDF 会用本机 OCR 自动识别文字。

格式转换
• 图片：JPG、PNG、HEIC、WEBP、TIFF、GIF、BMP、PDF
• PDF：转图片、文字、Markdown、Word、RTF（扫描件自动 OCR，支持中文）
• 文档：DOCX、PDF、RTF、Markdown、TXT、HTML、ODT、DOC
• PPT 和 Excel：转 PDF 或 Markdown
• 视频：MP4、MOV、GIF 动图、只提取音频
• 音频：M4A、WAV、AIFF、CAF

快捷工具
• 图片：裁剪（多种比例）、压缩、抠图、去除定位和相机信息、缩小一半、旋转、翻转、黑白、合并成 PDF
• PDF：压缩、按页拆分、提取文字、旋转、合并、去除作者信息
• 视频：压缩、转 720p、静音、提取音频、截帧
• 音频：压缩、转单声道

免费、开源、保护隐私
没有广告，不用注册，没有次数限制，没有内购。全部代码在 GitHub 上以 MIT 许可证开源。文件不会上传，也不收集任何数据，所有处理都用苹果系统自带的功能在你的 Mac 上完成。

FileFlipper 待在菜单栏里，可以从菜单栏图标暂停它，或设为开机启动。
```

---

## 常见被拒原因和应对

| 原因 | 应对 |
|---|---|
| 审核员找不到 App（菜单栏 App 没有窗口） | 备注里已经写了怎么找、怎么用 |
| 截图和实际 App 不符 | 截图都是用 App 自己的界面代码生成的；也可以换成你自己在 Mac 上截的图（⌘⇧5 → 选项 → 定时 5 秒，再去拖文件） |
| 和其他 App 太像（Guideline 4.3） | FileFlipper 有自己的名字、图标、代码；描述里不要提别的 App 的名字 |
| 隐私清单缺失 | 已经加好了（`Resources/PrivacyInfo.xcprivacy`） |
| 中国大陆缺少 ICP 备案号 | 见 B2，先取消中国大陆 |

---

## English summary

1. Xcode → Settings → Accounts: add your Apple Developer account.
2. Open `FileFlipper.xcodeproj` → target *FileFlipper* → *Signing & Capabilities* → pick your Team
   (bundle ID `com.aimeesun.fileflipper`, automatic signing).
3. ⌘R to test, then **Product → Archive → Distribute App → App Store Connect → Upload**.
4. In App Store Connect: subtitle, category Utilities / Productivity, price Free, App Privacy
   "Data Not Collected" with the privacy policy URL
   `https://github.com/Aimee51819/FileFlipper/blob/main/PRIVACY.md`, age rating 4+,
   screenshots from `docs/app-store-screenshots/`, the description, keywords and review notes above.
5. Select the uploaded build and **Submit for Review**. Bump `CURRENT_PROJECT_VERSION` for every new upload.
