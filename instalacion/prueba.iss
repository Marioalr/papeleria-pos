; Instalador de la prueba con una persona real (docs/plan.md). Lo arma GitHub Actions en Windows
; (.github/workflows/prueba.yml) con lo publicado en ..\publicar.
;
; - Como en la tienda (docs/plan.md, instalación y usuarios de Windows): lo instala el administrador para todos los
;   usuarios y quien prueba lo usa con una cuenta común de Windows. Sin servicio de Windows: la caja arranca su propio servicio
;   (puerto 5180) y lo apaga al cerrarse.
; - La base de prueba va en {app}\datos (solo lectura); la de trabajo y las respuestas, en
;   {commonappdata}\PapeleriaPos-Prueba, con escritura para los usuarios.
; - En el escritorio de todos: el acceso a la caja y la carpeta «Ejemplos de la prueba» (Léeme, factura XML y nota PDF).
; - Al desinstalar se borra todo, menos las respuestas que quien probó guardó en su escritorio.

#define Version GetEnv("VERSION_PRUEBA")
#if Version == ""
  #define Version "0.0.0"
#endif

[Setup]
AppId={{8E6D1C2A-5B7F-4E0A-9C3D-2F1B6A4E7D90}
AppName=Papelería POS (prueba)
AppVersion={#Version}
AppPublisher=Papelería POS
DefaultDirName={autopf}\PapeleriaPos-Prueba
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=admin
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

[Dirs]
Name: "{commonappdata}\PapeleriaPos-Prueba"; Permissions: users-modify

[Files]
Source: "..\publicar\servicio\*"; DestDir: "{app}\Servicio"; Flags: recursesubdirs ignoreversion
Source: "..\publicar\caja\*"; DestDir: "{app}\Caja"; Flags: recursesubdirs ignoreversion
Source: "caja-prueba.json"; DestDir: "{app}\Caja"; DestName: "caja.json"; Flags: ignoreversion
Source: "..\publicar\datos\prueba.db"; DestDir: "{app}\datos"; Flags: ignoreversion
Source: "..\publicar\ejemplos\*"; DestDir: "{commondesktop}\Ejemplos de la prueba"; Flags: ignoreversion

[Icons]
Name: "{commondesktop}\Papelería POS (prueba)"; Filename: "{app}\Caja\PapeleriaPos.Caja.exe"; WorkingDir: "{app}\Caja"
Name: "{commonprograms}\Papelería POS (prueba)"; Filename: "{app}\Caja\PapeleriaPos.Caja.exe"; WorkingDir: "{app}\Caja"

[Run]
; El conteo desde el celular: el servicio de la prueba recibe a los aparatos de la red local en el 5180.
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""Papeleria POS (prueba) - celulares"" dir=in action=allow program=""{app}\Servicio\PapeleriaPos.Servicio.exe"" protocol=TCP localport=5180 remoteip=localsubnet profile=any"; Flags: runhidden
; Como el usuario que lanzó el instalador, no como administrador.
Filename: "{commondesktop}\Ejemplos de la prueba\Léeme.txt"; Description: "Leer las instrucciones (usuarios y PIN)"; Flags: postinstall shellexec skipifsilent runasoriginaluser
Filename: "{app}\Caja\PapeleriaPos.Caja.exe"; Description: "Abrir la caja de prueba"; Flags: postinstall nowait skipifsilent runasoriginaluser

[UninstallRun]
; Si la caja se cerró mal, su servicio puede seguir abierto.
Filename: "{sys}\taskkill.exe"; Parameters: "/F /T /IM PapeleriaPos.Servicio.exe"; Flags: runhidden; RunOnceId: "CerrarServicio"
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""Papeleria POS (prueba) - celulares"""; Flags: runhidden; RunOnceId: "QuitarFirewall"

[UninstallDelete]
Type: filesandordirs; Name: "{app}"
Type: filesandordirs; Name: "{commonappdata}\PapeleriaPos-Prueba"
Type: filesandordirs; Name: "{commondesktop}\Ejemplos de la prueba"
