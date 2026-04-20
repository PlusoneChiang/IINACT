# IINACT TC (Traditional Chinese) 版本更新指南

本文件說明在 IINACT upstream 發布新版本（例如升級至 Dalamud API 13 + .NET 10）時，
維護 TC fork 所需確認的完整步驟。

---

## 一、更新策略

TC fork 由兩個子專案組成，各自維護：

```
你的 machina fork (PlusoneChiang/machina)
    ├── upstream (ravahn/machina)  ← TC/KR/Global opcodes 來源
    └── xom (marzent/machina)     ← Global Dalamud + 建置系統

你的 IINACT fork (PlusoneChiang/IINACT)
    ├── upstream IINACT            ← 最新框架與功能
    └── machina submodule          ← 指向上面的 machina fork
```

### 完整更新流程：

```
【machina 先更新】
git fetch upstream + git fetch xom
    ↓
merge upstream/master（TC/KR/Global opcodes）
    ↓
merge xom/dalamud（若有 Dalamud 建置更新）
    ↓
降版 machina runtime（若需要）
    ↓
push 到 PlusoneChiang/machina

【IINACT 再更新】
取得最新 upstream IINACT
    ↓
降版 IINACT 框架與 runtime（若 TC Dalamud 尚未跟進）
    ↓
還原 TC 專屬修改（本文件 Step 3）
    ↓
更新 machina submodule 指向新版
    ↓
建置 & 測試
```

---

## 二、框架降版（從 API 14/Net10 → API 12/Net9）

### 2-1. .NET Runtime 降版

修改以下所有 `.csproj` 的 `<TargetFramework>`：

| 專案 | 路徑 | 改為 |
|---|---|---|
| IINACT | `IINACT/IINACT.csproj` | `net9.0-windows` |
| OverlayPlugin.Core | `OverlayPlugin.Core/OverlayPlugin.Core.csproj` | `net9.0-windows` |
| OverlayPlugin.Common | `OverlayPlugin.Common/OverlayPlugin.Common.csproj` | `net9.0-windows` |
| NotACT | `NotACT/NotACT.csproj` | `net9.0-windows` |
| FetchDependencies | `FetchDependencies/FetchDependencies.csproj` | `net9.0-windows` |
| Machina | `machina/Machina/Machina.csproj` | `net9.0` |
| Machina.FFXIV | `machina/Machina.FFXIV/Machina.FFXIV.csproj` | `net9.0` |

> **注意**：Test 專案（`Machina.Tests`、`Machina.FFXIV.Tests`）不影響發布，可保持較新版本。

### 2-2. Dalamud API 版本

修改 `repo.json`：

```json
"DalamudApiLevel": 12
```

確認 `DalamudLibPath` 指向的 Dalamud dev 目錄使用正確的 API 12 版本（TC Dalamud fork）。

---

## 三、TC 專屬程式碼修改（每次 upstream 更新後必須確認）

### 3-1. `IINACT/Plugin.cs` — 強制 TC 地區

```csharp
// 找到這行，確認存在：
OpcodeManager.Instance.SetRegion(GameRegion.TraditionalChinese);
```

若被 upstream 覆蓋，在 Plugin constructor 的 `Log = pluginLog;` 後一行加回。

### 3-2. `IINACT/FfxivActPluginWrapper.cs` — 地區與語言設定

確認以下兩處：

**語言映射**（`ClientLanguage` property，約第 136 行）：

```csharp
private Language ClientLanguage =>
    dalamudClientLanguage switch
    {
        Dalamud.Game.ClientLanguage.Japanese => Language.Japanese,
        Dalamud.Game.ClientLanguage.English  => Language.English,
        Dalamud.Game.ClientLanguage.German   => Language.German,
        Dalamud.Game.ClientLanguage.French   => Language.French,
        _ when dalamudClientLanguage.ToString() == "ChineseSimplified"  => Language.Chinese,
        _ when dalamudClientLanguage.ToString() == "TraditionalChinese" => Language.TraditionalChinese,
        _ => Language.English
    };
```

> **為何用 `.ToString()` 比較**：TC Dalamud fork 有 `ClientLanguage.SimplifiedChinese` 和 `ClientLanguage.TraditionalChinese`，
> 標準 Dalamud SDK 無這些值，用字串比較可避免編譯期的 enum 衝突。

**地區設定**（`SetupSettingsMediator` 方法，約第 165 行）：

```csharp
RegionID = Region.TraditionalChinese,
```

### 3-3. `IINACT/Network/ZoneDownHookManager.cs` — 核心封包解混淆

這是最關鍵的修改，防止 plugin 在 TC 啟動時崩潰。

**原因**：TC binary 沒有靜態 `OpcodeKeyTable`，全域簽章 `OpcodeKeyTableSignature` 不存在。

確認以下三段程式碼存在：

**① 欄位宣告**（class 頂端）：
```csharp
private readonly bool isTraditionalChinese;
```

**② Constructor 中的 TC 路徑**（在 `else if (isTraditionalChinese)` 區塊）：
```csharp
isTraditionalChinese = gameRegion == Machina.FFXIV.GameRegion.TraditionalChinese;

// ...

else if (isTraditionalChinese)
{
    Plugin.Log.Warning("[ZoneDownHookManager] TraditionalChinese region: using dynamic 3-entry key table from PacketDispatcher");
    versionConstants = GetFallbackVersionConstant(0, 12);
    unscrambler = new Unscrambler73();
    unscrambler.Initialize(versionConstants);
}

// ...

if (isTraditionalChinese)
{
    opcodeKeyTable = new int[3];
    Plugin.Log.Debug("[ZoneDownHookManager] TC: opcodeKeyTable will be populated from PacketDispatcher keys");
}
```

**③ `UpdateKeys()` 中的 TC key table 同步**：
```csharp
if (isTraditionalChinese)
{
    opcodeKeyTable[0] = key0;
    opcodeKeyTable[1] = key1;
    opcodeKeyTable[2] = key2;
}
```

> **技術背景**：TC `OnReceivePacket` 在 offset `+0x195` 用 `opcode % 3` 索引
> `PacketDispatcher.Key0/Key1/Key2`（struct offset `0x20/0x24/0x28`）作為 key table，
> 而非 Global 的靜態 module-relative table。

---

## 四、TC Opcode 更新（遊戲版本更新時）

**檔案**：`machina/Machina.FFXIV/Headers/Opcodes/TraditionalChinese.txt`

TC opcodes 由 **ravahn (`upstream/master`)** 維護，與 Korean opcodes 一同更新。
每次 TC 遊戲版本更新後需更新此檔案。

### 4-1. Machina 三個 Remote 的責任

| Remote | 來源 | 維護內容 |
|---|---|---|
| `upstream` | ravahn/machina | `TraditionalChinese.txt` + `Korean.txt` + `Global.txt` + struct 更新 |
| `xom` | marzent/machina | `Global.txt` Dalamud 版本 + 建置系統（runtime 設定） |
| `origin` | PlusoneChiang/machina | TC branch = merge 以上兩者 + runtime 降版 |

### 4-2. Machina 更新步驟

```bash
cd machina

# 取得最新版本
git fetch upstream   # TC/KR/Global opcodes 來源（最關鍵）
git fetch xom        # Dalamud 建置系統更新

# 合併 upstream（TC opcodes 在這裡，配合 Korean 一起更新）
git merge upstream/master

# 合併 xom（若有 Dalamud/建置相關更新）
git merge xom/dalamud

# 若新版 runtime 升到 net10，降回 net9
# 修改 Machina.FFXIV/Machina.FFXIV.csproj：net10.0 → net9.0
# 修改 Machina/Machina.csproj：net10.0 → net9.0

# push 更新
git push origin tc/net9

# 回到 IINACT，更新 submodule 指向
cd ..
git submodule update --remote machina
# 或手動 cd machina && git checkout <new_commit>
```

### 4-3. 目前版本（`2026.03.12.0000.0000`）關鍵 opcodes：

| Opcode 名稱 | 值 | 說明 |
|---|---|---|
| PlayerSpawn | 0x3B2 | 玩家進場 |
| NpcSpawn | 0x1D8 | NPC/怪物進場 |
| NpcSpawn2 | 0x2C2 | NPC/怪物進場（次要） |
| Ability1 | 0xC8 | 單體技能效果 |
| Ability8 | — | 8連技能效果 |
| StatusEffectList | 0x154 | 狀態效果列表 |
| ActorControl | 0x18D | 角色控制 |
| ActorCast | 0x1ED | 詠唱 |

### 如何取得新版 opcodes：

1. 參考 [FFXIVOpcodes](https://github.com/karashiiro/FFXIVOpcodes) 或 TC 社群的 opcode 分析
2. 或用 Wireshark + 封包比對方式自行分析

---

## 五、Binary 簽章分析（opcodes 以外的底層問題）

若 TC 遊戲大改版（major patch）後 plugin 崩潰，需重新確認：

### 5-1. `GenericDownSignature` 是否仍有效

**簽章**：`E8 ?? ?? ?? ?? 4C 8B 4F 10 8B 47 1C 45`

用 binary 搜尋工具（如 010 Editor 或 Python）在新版 `ffxiv_dx11.exe` 確認仍有 **3 個** match。

```python
import re
with open("ffxiv_dx11.exe", "rb") as f:
    data = f.read()
pattern = bytes.fromhex("4C8B4F108B471C45")
matches = [m.start() for m in re.finditer(re.escape(pattern), data)]
print(f"找到 {len(matches)} 個 match")  # 預期 3 個，ZoneDownHook 用 index [2]
```

### 5-2. `PacketDispatcher` vtable 位移是否仍正確

TC 的 `PacketDispatcher` struct 關鍵欄位（FFXIVClientStructs 定義）：

| 欄位 | Offset | 用途 |
|---|---|---|
| Key0 | 0x20 | TC key table[0] |
| Key1 | 0x24 | TC key table[1] |
| Key2 | 0x28 | TC key table[2] |
| GameRandom | 0x18 | 解混淆計算 |
| LastPacketRandom | 0x1C | 解混淆計算 |

若 FFXIV 大版本更新更動了 CharacterStruct layout，需重新確認這些 offset。

---

## 六、建置與驗證

```bash
# 建置
dotnet build IINACT/IINACT.csproj -c Release

# 預期結果：0 errors（3 個 pre-existing warnings 可忽略）
```

啟動後在 Dalamud log 中確認以下訊息：

```
[ZoneDownHookManager] TraditionalChinese region: using dynamic 3-entry key table from PacketDispatcher
[ZoneDownHookManager] TC: opcodeKeyTable will be populated from PacketDispatcher keys
```

---

## 七、已知限制與待確認事項

| 項目 | 狀態 | 說明 |
|---|---|---|
| 解混淆正確性 | ⚠️ 未完整驗證 | TC `OnReceivePacket` 在 `+0x21A` 用靜態常數 `XOR 0xF1E2D9C8`，與 Unscrambler73 預期不同。戰鬥封包可能仍有問題 |
| PacketDispatcher 初始化時序 | ⚠️ 未驗證 | Plugin 載入早期 `GetInstance()` 可能回傳 null |
| TC locale 在 cactbot overlay | ℹ️ 設計決策 | TC 沒有獨立 locale → fallback 到 English intl（`FFXIVProcessIntl` offset 正確） |
| FetchDependencies CN URL | ℹ️ 無需修改 | TC 使用 Global FFXIV_ACT_Plugin（iinact.com），非 cninact.diemoe.net |

---

## 八、快速 Checklist

### Machina 更新

- [ ] `git fetch upstream && git fetch xom`
- [ ] `git merge upstream/master`（TC opcodes 在這裡）
- [ ] `git merge xom/dalamud`（若有 Dalamud 相關更新）
- [ ] 若 runtime 升版：`Machina.FFXIV.csproj` + `Machina.csproj` 降回 `net9.0`
- [ ] `git push origin tc/net9`

### IINACT 更新

每次 upstream IINACT 更新後，依序確認：

- [ ] `git submodule update --remote machina`（或手動指向新 machina commit）
- [ ] `repo.json`：`DalamudApiLevel` = 12
- [ ] 所有 `.csproj`：`TargetFramework` = `net9.0-windows` / `net9.0`
- [ ] `Plugin.cs`：`SetRegion(GameRegion.TraditionalChinese)` 存在
- [ ] `FfxivActPluginWrapper.cs`：`ClientLanguage` switch 有 `TraditionalChinese` case
- [ ] `FfxivActPluginWrapper.cs`：`RegionID = Region.TraditionalChinese`
- [ ] `ZoneDownHookManager.cs`：`isTraditionalChinese` 欄位與三段 TC 路徑存在
- [ ] `TraditionalChinese.txt`：若遊戲有更版，確認 opcode 數值已更新
- [ ] `dotnet build` 0 errors
- [ ] 遊戲啟動後 log 顯示 TC 初始化訊息
