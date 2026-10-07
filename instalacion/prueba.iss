; Instalador de la prueba con una persona real (docs/plan.md). Lo arma GitHub Actions en Windows
; (.github/workflows/prueba.yml) con lo publicado en ..\publicar.
;
; - Se instala para el usuario, sin permisos de administrador ni servicio de Windows: la caja arranca su propio servicio
;   (puerto 5180) y lo apaga al cerrarse.
; - La base de prueba va en {app}\datos; la de trabajo se crea junto a ella la primera vez.
; - En el escritorio: el acceso a la caja y la carpeta «Ejemplos de la prueba» (Léeme, factura XML y nota PDF).
; - Al desinstalar se borra todo, menos las respuestas que quien probó guardó en el escritorio.

#define Version GetEnv("VERSION_PRUEBA")
#if Version == ""
  #define Version "0.0.0"
#endif

[Setup]
AppId={{8E6D1C2A-5B7F-4E0A-9C3D-2F1B6A4E7D90}
AppName=Papelería POS (prueba)
AppVersion={#Version}
AppPublisher=Papelería POS
DefaultDirName={localappdata}\PapeleriaPos-Prueba
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=..\publicar
OutputBaseFilename=PapeleriaPos-Prueba
Compression=lzma2/max
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
WizardStyle=modern
UninstallDisplayName=Papelería POS (prueba)
CloseApplications=force

[Languages]
Name: "es"; MessagesFile: "compiler:Languages\Spanish.isl"

[Files]
Source: "..\publicar\servicio\*"; DestDir: "{app}\Servicio"; Flags: recursesubdirs ignoreversion
Source: "..\publicar\caja\*"; DestDir: "{app}\Caja"; Flags: recursesubdirs ignoreversion
Source: "caja-prueba.json"; DestDir: "{app}\Caja"; DestName: "caja.json"; Flags: ignoreversion
Source: "..\publicar\datos\prueba.db"; DestDir: "{app}\datos"; Flags: ignoreversion
Source: "..\publicar\ejemplos\*"; DestDir: "{userdesktop}\Ejemplos de la prueba"; Flags: ignoreversion

[Icons]
Name: "{userdesktop}\Papelería POS (prueba)"; Filename: "{app}\Caja\PapeleriaPos.Caja.exe"; WorkingDir: "{app}\Caja"
Name: "{userprograms}\Papelería POS (prueba)"; Filename: "{app}\Caja\PapeleriaPos.Caja.exe"; WorkingDir: "{app}\Caja"

[Run]
Filename: "{userdesktop}\Ejemplos de la prueba\Léeme.txt"; Description: "Leer las instrucciones (usuarios y PIN)"; Flags: postinstall shellexec skipifsilent
Filename: "{app}\Caja\PapeleriaPos.Caja.exe"; Description: "Abrir la caja de prueba"; Flags: postinstall nowait skipifsilent

[UninstallRun]
; Si la caja se cerró mal, su servicio puede seguir abierto.
Filename: "{sys}\taskkill.exe"; Parameters: "/F /T /IM PapeleriaPos.Servicio.exe"; Flags: runhidden; RunOnceId: "CerrarServicio"

[UninstallDelete]
Type: filesandordirs; Name: "{app}"
Type: filesandordirs; Name: "{userdesktop}\Ejemplos de la prueba"
