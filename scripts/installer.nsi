Unicode true
!include "MUI2.nsh"
!ifndef APP_SOURCE
  !error "Pass /DAPP_SOURCE=<published application folder>"
!endif
!ifndef RELEASE_DIR
  !error "Pass /DRELEASE_DIR=<release output folder>"
!endif
!ifndef APP_VERSION
  !error "Pass /DAPP_VERSION=<application version>"
!endif
!ifndef UNINSTALL_LIST
  !error "Pass /DUNINSTALL_LIST=<generated file list>"
!endif

Name "Scenery Atlas ${APP_VERSION}"
OutFile "${RELEASE_DIR}\SceneryAtlas-${APP_VERSION}-Setup.exe"
InstallDir "$LOCALAPPDATA\Programs\Scenery Atlas"
RequestExecutionLevel user
SetCompressor /SOLID zlib
ShowInstDetails show
ShowUninstDetails show
VIProductVersion "${APP_VERSION}.0"
VIAddVersionKey "ProductName" "Scenery Atlas"
VIAddVersionKey "FileDescription" "Scenery Atlas Installer"
VIAddVersionKey "FileVersion" "${APP_VERSION}"
VIAddVersionKey "ProductVersion" "${APP_VERSION}"
VIAddVersionKey "LegalCopyright" "Copyright 2026 Scenery Atlas contributors"

!define MUI_ABORTWARNING
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "English"

Section "Install"
  SetOutPath "$INSTDIR"
  File /r "${APP_SOURCE}\*"
  WriteUninstaller "$INSTDIR\Uninstall.exe"
  CreateDirectory "$SMPROGRAMS\Scenery Atlas"
  CreateShortCut "$SMPROGRAMS\Scenery Atlas\Scenery Atlas.lnk" "$INSTDIR\SceneryAtlas.exe"
  CreateShortCut "$SMPROGRAMS\Scenery Atlas\Uninstall Scenery Atlas.lnk" "$INSTDIR\Uninstall.exe"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\SceneryAtlas" "DisplayName" "Scenery Atlas"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\SceneryAtlas" "DisplayVersion" "${APP_VERSION}"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\SceneryAtlas" "Publisher" "Scenery Atlas"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\SceneryAtlas" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\SceneryAtlas" "UninstallString" "$\"$INSTDIR\Uninstall.exe$\""
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\SceneryAtlas" "QuietUninstallString" "$\"$INSTDIR\Uninstall.exe$\" /S"
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\SceneryAtlas" "NoModify" 1
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\SceneryAtlas" "NoRepair" 1
SectionEnd

Section "Uninstall"
  Delete "$SMPROGRAMS\Scenery Atlas\Scenery Atlas.lnk"
  Delete "$SMPROGRAMS\Scenery Atlas\Uninstall Scenery Atlas.lnk"
  RMDir "$SMPROGRAMS\Scenery Atlas"
  DeleteRegKey HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\SceneryAtlas"
  !include "${UNINSTALL_LIST}"
  Delete "$INSTDIR\Uninstall.exe"
  RMDir "$INSTDIR"
  ; Local accounts and library data stay under %LOCALAPPDATA%\SceneryAtlas.
SectionEnd
