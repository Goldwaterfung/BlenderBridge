# Blender Bridge

[English](README.md) | [繁體中文](README.zh-TW.md)

Blender Bridge 可將 Blender 連接至 Unity 編輯器，提供即時網格與材質預覽，也能將選取的靜態網格匯出為 FBX 檔案至 Unity 專案。Unity 匯入 FBX 後，可將其烘焙成可重複使用、包含材質的 Prefab。

<video controls width="100%" preload="metadata">
  <source src="images/showcase.mp4" type="video/mp4">
</video>

## 系統需求

- Blender 5.0 或更新版本（外掛宣告的最低版本為 Blender 5.0）。
- Unity 編輯器專案。
- Blender 與 Unity 必須在同一台電腦上執行。連線使用本機迴路網路，預設連接埠為 `8191`。

## 安裝

### Unity

1. 將 `UnityBlenderBridge` 資料夾複製到 Unity 專案的 `Assets` 資料夾，例如：

   ```text
   MyUnityProject/
   └── Assets/
       └── UnityBlenderBridge/
   ```

2. 等待 Unity 編譯指令碼。
3. 開啟 **Window > Blender Bridge > Status**。Unity 伺服器通常會自動啟動；若狀態顯示 **Stopped**，請按一下 **Start**。

![在 Unity 中開啟 Blender Bridge 狀態視窗](images/step1_blender_bridge_menu_unity.jpg)

狀態視窗會顯示連線狀態、監聽連接埠、目前連線的端點、即時物件與傳輸資訊。連線期間可保持視窗開啟，以便查看狀態。

![Unity Blender Bridge 狀態視窗](images/step2_blender_bridge_window_unity.jpg)

### Blender 外掛

1. 在 Blender 中開啟 **Edit > Preferences**。
2. 開啟 **Add-ons**（或依 Blender 版本顯示為 **Extensions**），然後選擇 **Install from Disk**。
3. 選取本專案中的 `BlenderBridge-addon.zip`。
4. 若外掛尚未啟用，請在外掛清單中啟用 **Blender Bridge**。
5. 在 3D 視圖中按下 **N** 開啟側邊欄，並選取 **Blender Bridge** 分頁。

![從 Edit 選單開啟 Blender 偏好設定](images/step1_install_blender_addon_menu.jpg)

![在 Blender 偏好設定中選擇 Install from Disk](images/step2_install_blender_addon_window.jpg)

![選取 BlenderBridge-addon.zip](images/step3_install_blender_addon.jpg)

![3D 視圖側邊欄中的 Blender Bridge 控制項](images/step4_blender_addon_panel.jpg)

## 即時預覽

1. 在 Unity 的 Blender Bridge 狀態視窗中，確認伺服器正在監聽連接埠 `8191`（或記下已設定的連接埠）。
2. 在 Blender 的 **Blender Bridge** 側邊欄分頁中，將 **Target Port** 設為相同的連接埠。
3. 選擇即時 **Export Scope**：
   - **Selected objects**（預設）：選取的網格物件及其必要的上層物件。
   - **Active collection**：作用中集合中的網格物件。
   - **Whole scene**：作用中場景中的網格物件。
4. 將 **Unit Scale Override** 保持為 `0`，以使用 Blender 場景的單位比例；或輸入大於 `0` 的值覆寫比例。
5. 按一下 **Start Live Updates**。Blender 會連線至 Unity 並傳送目前場景快照；即時更新啟用期間，後續變更也會持續傳送。
6. 完成後按一下 **Stop Live Updates**。

即時連線僅用於 Unity 編輯器中的預覽，不會自行建立已儲存的 FBX 或 Prefab。

## 匯出 FBX 並建立 Prefab

1. 依照上述步驟連線至 Unity，並啟用即時更新。
2. 在 Blender 中選取要匯出的靜態網格物件。在 Blender Bridge 面板中，將 **Output Path** 設定為連線中 Unity 專案 `Assets` 資料夾下的路徑。預設值為 `Art/Blender/Environment`。
3. 可選擇輸入 **File Name**；留白時會自動依名稱產生檔名。
4. 按一下 **Bake to Unity**。外掛會將 FBX 寫入 Unity 專案的 Assets 資料夾。
5. 在 Unity 等待 FBX 匯入完成。在 Project 視窗中選取該 FBX，然後從其右鍵選單選擇 **Assets > Blender Bridge > Bake FBX To Prefab**。

Unity 烘焙程序會在已設定的 Blender Bridge 輸出資料夾中建立 Prefab 與材質。此匯出流程支援靜態網格，不會烘焙動畫。

## 功能限制

- 場景與資產更新只會由 Blender 傳送至 Unity；Unity 端的場景編輯不會同步回 Blender。Unity 可以傳送橋接控制要求，例如要求重新同步，但這不代表支援雙向編輯。
- 每個 Unity 編輯器執行個體同時只能接受一個 Blender 連線。
- FBX 烘焙僅支援靜態網格。不支援骨架綁定或動畫網格、形狀鍵，以及具有骨架、曲線或粒子系統修改器的網格。烘焙前請先儲存 Blender 檔案。
- Blender 的 **Bake to Unity** 功能只會匯出 FBX；Unity 必須另外匯入 FBX，並執行 **Bake FBX To Prefab**，才能建立 Prefab 與材質。

## 未來規劃

以下是未來可能的開發方向，並非已承諾的功能或發布時程：

- 擴充靜態網格以外的匯出與烘焙支援，例如骨架綁定或動畫內容。
- 評估雙向同步，讓選定的 Unity 端編輯可以傳回 Blender。
- 簡化 Unity 中的 FBX 匯入與 Prefab 建立流程。

## 設定

Unity 預設監聽連接埠 `8191`，輸出資料夾為 `Assets/Art/Blender`。若要自訂 Unity 連接埠、輸出資料夾或碰撞器行為，請使用 **Assets > Create > Blender Bridge > Settings** 建立設定資產。專案中只能保留一個 Blender Bridge Settings 資產。Blender 的目標連接埠必須與 Unity 相同；若要使用 Unity Prefab 烘焙功能，FBX 輸出路徑必須位於已設定的輸出資料夾內。

## 疑難排解

- **Blender 一直顯示未連線：**確認 Unity 狀態視窗顯示 **Listening** 或 **Connected**，且兩邊使用相同連接埠。Blender 與 Unity 必須在同一台電腦上執行。
- **Unity 未開始監聽：**查看狀態視窗中的錯誤訊息，並確認設定的連接埠有效且未被其他程式使用。
- **Blender 已連線，但沒有預覽：**檢查 Blender 的 **Export Scope** 是否包含網格物件。使用 **Selected objects** 時，請至少選取一個網格物件。
- **無法使用或執行「Bake to Unity」失敗：**請先啟用即時更新、選取靜態網格物件，並確認輸出路徑位於 Unity 專案的 `Assets` 資料夾及已設定的 Blender Bridge 輸出資料夾內。
- **Unity 的 Prefab 烘焙失敗：**確認 FBX 已匯入至設定的輸出資料夾，且專案可使用 **Universal Render Pipeline/Lit** 著色器。

## 專案內容

- `UnityBlenderBridge/` — Unity 執行階段與編輯器整合。
- `BlenderBridge-addon.zip` — 可安裝的 Blender 外掛。
- `images/` — 本指南使用的安裝與操作截圖。
- `blender_bridge/LICENSE` — MIT 授權條款。
