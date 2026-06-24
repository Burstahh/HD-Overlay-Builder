Optional embedded font resources
================================

HD Overlay Builder can run without embedded Noto Sans font files. If the expected
Noto Sans files are not present, the app falls back to installed Noto Sans or
Segoe UI.

For a local embedded-font build, place Noto_Sans.zip beside
PREPARE_NOTO_SANS_WINDOWS.bat, then run:

  PREPARE_NOTO_SANS_WINDOWS.bat
  BUILD_WINDOWS.bat

Expected embedded files after preparation:

  Resources\Fonts\NotoSans-Regular.ttf
  Resources\Fonts\NotoSans-Bold.ttf
  Resources\Fonts\NotoSans-Italic.ttf
  Resources\Fonts\NotoSans-BoldItalic.ttf
  Resources\Fonts\OFL_NOTO_SANS.txt

Noto Sans is distributed by Google under the SIL Open Font License. Keep the OFL
license text with any font files you redistribute.
