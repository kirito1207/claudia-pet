# Claudia 抱 Clawd 图标

基于 CrossPet 的 Claude 娘头像，以及项目中 `characters/claude/clawd.webp` 的抱 Clawd 立绘，使用内置 image_gen 整理为单人透明大头照。继承角色素材的 CC BY-NC-SA 4.0 授权，出处见项目 [NOTICE.md](../NOTICE.md)。

- AppIcon.png：透明原图。
- icon-source.webp：同一图像的无损 WebP。
- AppIcon.ico：程序与托盘用，包含 16、20、24、32、40、48、64、128、256 像素版本。

图标由 `scripts/build.ps1` 嵌入程序 EXE，托盘读取随程序分发的同一份 `AppIcon.ico`。

生成方式：内置 image_gen（非 CLI）；完成后只做图标格式与尺寸转换。

## 实际提示词

Edit target: image 1 is the current Claudia single-character application icon. Supporting reference: image 2 is the project's existing full-body illustration of Claudia hugging Clawd. Update image 1 so her two hands are clearly hugging the SAME orange Clawd plush from image 2, placed in the lower foreground at her chest. Clawd must retain its distinctive flat orange pixel/block-shaped crab silhouette, two black square eyes, and short block legs; it is a toy plush, not a real crab, and has no mouth. Preserve image 1's recognizable large head, orange hair, amber eyes, head tilt, small neutral mouth, daisy hair ornament, black bow and white ribbons, detailed anime linework, colors, and transparent background. Make hands/arms naturally hold the plush with white floral sleeves matching reference 2. Keep a square, clean Windows application icon composition: mostly head plus a small upper body, Clawd clearly visible at bottom center, no full body, no feet, no book. Keep the entire head/flower and toy silhouette within a small transparent safety margin. The head remains dominant and readable at tiny tray sizes. No text, logo, frame, other characters or background. Do not copy the full-body framing of image 2. Do not redesign Claudia or Clawd.
