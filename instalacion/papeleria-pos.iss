; El instalador de Papelería POS para la tienda (docs/instalacion.md): PapeleriaPos-Instalador-1.0.<corrida>.exe. Lo arma GitHub
; Actions en Windows (.github/workflows/prueba.yml) con lo mismo que el paquete para actualizar, de la misma corrida.
;
; - Lo corre el administrador de la PC, para todas sus cuentas; pregunta qué es esta PC («Caja principal», «Otra caja» o «La única
;   caja»), sus impresoras, el lector, Control Print y eleventa, en español sencillo, y salta lo que no aplica.
; - El trabajo lo hacen los scripts de siempre (los mismos que se corren a mano, docs/pruebas-en-tienda.md, y que prueba el flujo
;   «Instalación»), a través de instalar-pc.ps1: instalar-servicio.ps1 e instalar-caja.ps1 la primera vez, actualizar.ps1 (con su
;   respaldo de la base y su vuelta atrás) si ya estaba. servicio.json y caja.json los escribe el ayudante
;   (PapeleriaPos.Instalacion.exe, src/PapeleriaPos.Instalacion): solo lo que cambió, con sus comentarios.
; - La clave de Firebird y el código de Control Print van directo de su casilla a las variables del servicio (el registro de Windows):
;   nunca en una línea de órdenes, un archivo ni un registro. En silencio se toman de las variables de entorno
;   PAPELERIAPOS_CLAVE_ELEVENTA y PAPELERIAPOS_CODIGO_CONTROLPRINT.
; - La base de la tienda se muda de una PC a otra con tres corridas de este mismo instalador y una carpeta, el «paquete de mudanza» (por lo
;   común, en la USB que lo lleva): en la página «La base de la tienda» se saca en la PC que la tiene, se trae en la nueva y, de regreso en la
;   primera, se termina (queda como «Otra caja», con su misma alta) o se cancela. El asistente solo pregunta y enseña: lo hacen
;   instalar-pc.ps1 y el ayudante, que antes de cambiar nada revisa el paquete (docs/instalacion.md).
; - Sin preguntar (el CI): /VERYSILENT /SUPPRESSMSGBOXES y las respuestas como /ROL=principal… (docs/instalacion.md, «Sin preguntar»).
;   Cómo quedó: el código de salida y C:\ProgramData\PapeleriaPos\instalador\resultado.txt.
; - Desinstalar quita el programa, el servicio, el firewall y el acceso; la base, los respaldos y la configuración se quedan salvo
;   que se marque «Borrar también los datos». Nunca toca eleventa ni Control Print. A media mudanza no deja borrar los datos: se niega
;   antes de quitar nada (la caja de esa PC puede tener ventas sin subir).

#ifndef Version
  #define Version "0.0.0-local"
#endif

[Setup]
AppId={{F7B6F872-3E72-4188-96CF-D93827097657}
AppName=Papelería POS
AppVersion={#Version}
AppVerName=Papelería POS {#Version}
AppPublisher=Papelería POS
DefaultDirName={autopf}\PapeleriaPos
UsePreviousAppDir=no
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableWelcomePage=no
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir=..\publicar
OutputBaseFilename=PapeleriaPos-Instalador-{#Version}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
SetupLogging=yes
CloseApplications=no
; Una sola copia a la vez (dos a la par correrían dos actualizar.ps1). InitializeSetup lo dice antes, en palabras sencillas.
SetupMutex=PapeleriaPosInstalador,Global\PapeleriaPosInstalador
UninstallDisplayName=Papelería POS
UninstallDisplayIcon={app}\Caja\PapeleriaPos.Caja.exe

[Languages]
Name: "es"; MessagesFile: "compiler:Languages\Spanish.isl"

[Files]
; Primero lo que se saca mientras pregunta (con la compresión sólida, lo de arriba sale sin descomprimir lo demás).
Source: "..\publicar\instalacion\PapeleriaPos.Instalacion.exe"; Flags: dontcopy
Source: "..\publicar\webview2\MicrosoftEdgeWebview2Setup.exe"; Flags: dontcopy
; Lo que se queda para desinstalar (y para la próxima vez).
Source: "instalar-pc.ps1"; DestDir: "{app}\instalador"; Flags: ignoreversion
Source: "instalar-servicio.ps1"; DestDir: "{app}\instalador"; Flags: ignoreversion
Source: "instalar-caja.ps1"; DestDir: "{app}\instalador"; Flags: ignoreversion
; El paquete, como el de actualizar (actualizar.ps1, servicio\ y caja\), a la carpeta temporal: los scripts lo ponen en Archivos de
; programa. Se borra al terminar.
Source: "..\publicar\actualizar.ps1"; DestDir: "{tmp}\paquete"; Flags: ignoreversion uninsneveruninstall
Source: "..\publicar\servicio\*"; DestDir: "{tmp}\paquete\servicio"; Flags: recursesubdirs ignoreversion uninsneveruninstall
Source: "..\publicar\caja\*"; DestDir: "{tmp}\paquete\caja"; Flags: recursesubdirs ignoreversion uninsneveruninstall

[Run]
; Como el usuario que abrió el instalador, no como administrador: ahí se termina (la tienda, la cuenta del dueño, los costos).
Filename: "{app}\Caja\PapeleriaPos.Caja.exe"; WorkingDir: "{app}\Caja"; Description: "Abrir la caja para terminar"; Flags: postinstall nowait skipifsilent runasoriginaluser; Check: SePuedeAbrirLaCaja

[Code]
const
  ClaveDelServicio = 'SYSTEM\CurrentControlSet\Services\PapeleriaPos';
  ClaveWebView2 = 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';
  ClaveDesinstalar = 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{F7B6F872-3E72-4188-96CF-D93827097657}_is1';
  VersionDelInstalador = '{#Version}';
  SinValor = '~';
  ColorDeAviso = $000090;
  MutexDelInstalador = 'PapeleriaPosInstalador,Global\PapeleriaPosInstalador';
  { Lo que dice «Dónde está la caja principal» sobre su casilla (al terminar una mudanza dice otra cosa: ahí la dirección hace falta). }
  DondeEstaLaPrincipal = 'Su IP (por ejemplo, 192.168.1.10). Si no la sabes, búscala en la red; o déjala vacía y la caja la busca al abrirla.';

var
  { Lo que dijo el ayudante de esta PC (leer) y lo que dijo instalar-pc.ps1 en cada paso. }
  Actual, Paso1, Paso2: TArrayOfString;
  ImpresorasNombres, ImpresorasAvisos, GbakRutas, GbakVersiones, GbakAvisos, Encontradas, EncontradasAvisos: TArrayOfString;
  HayPos, HabiaClave, HabiaCodigo, ProgramaBien: Boolean;
  CodigoDeSalida, CodigoPaso1, CodigoPaso2: Integer;
  Roles, WebView2Dice, ClaveDice, CodigoDice, Registro: String;
  { Sin preguntar: la primera respuesta que no se puede usar. PrepareToInstall termina con ella sin cambiar nada (sale con 7). }
  FalloSinPreguntar: String;
  BorrarDatosAlQuitar: Boolean;
  { La mudanza de la base: lo que dijo el ayudante (mudanza-ver) del paquete de la carpeta escrita, y de qué carpeta lo dijo; el paquete con
    que se llenaron las páginas de Control Print y de eleventa, y si su puente llega prendido y escribiendo; si se aceptó traer la base encima
    de la tienda que ya tiene esta PC; si se confirmó cancelar aunque la USB diga que la base ya se trajo a la otra PC; y si ya se entró a su
    página y ya se propuso la dirección de la caja principal. }
  Paquete: TArrayOfString;
  PaqueteVisto, PaqueteAplicado: String;
  ViendoElPaquete, PuenteMudado, PuenteMudadoEscribe, MudanzaEncima, CancelarAunqueTraida, MudanzaVista, PrincipalPropuesta: Boolean;
  { Mientras corre el ayudante el asistente sigue atendiendo sus botones: con una revisión a medias, otro «Siguiente» no cuenta (NextButtonClick). }
  Ocupado: Boolean;

  PaginaRol, PaginaMudanza, PaginaPrincipal, PaginaImpresoras, PaginaLector, PaginaControlPrint, PaginaEleventa, PaginaEleventa2, PaginaResultado: TWizardPage;
  RolPrincipal, RolOtra, RolUnica, Mudanza1, Mudanza2, Mudanza3, LectorTeclado, LectorCom, CpNo, CpSi, ElNo, ElSi: TNewRadioButton;
  NotaRol, EtiquetaPaquete, NotaMudanza, EtiquetaPrincipal, EstadoPrincipal, EtiquetaTicketIp, AvisoCola, NotaLector, NotaCodigo, NotaEleventa, NotaGbak,
    NotaClave: TNewStaticText;
  EditPaquete, EditPrincipal, EditTicketIp, EditCpServidor, EditElServidor, EditElBase, EditElCaja, EditElCajero, EditRespaldos: TNewEdit;
  EditCodigo, EditClave: TPasswordEdit;
  ComboTicket, ComboCola, ComboPuerto, ComboBaudios, ComboGbak: TNewComboBox;
  BotonPaquete, BotonBuscar, BotonProbar, BotonGbak, BotonRespaldos: TNewButton;
  ListaEncontradas: TNewListBox;
  MemoResultado: TNewMemo;

{ ── Lo de siempre ── }

{ Dice algo. En silencio solo queda en el registro de Inno (/LOG): nunca una ventana que espere. }
procedure Avisar(const Texto: String; const Tipo: TMsgBoxType);
begin
  Log(Texto);
  if not WizardSilent then
    MsgBox(Texto, Tipo, MB_OK);
end;

{ Una respuesta que no se puede usar. Con el asistente a la vista se dice y no se avanza. Sin preguntar se anota la primera y se sigue:
  PrepareToInstall termina con ella, sin cambiar nada (sale con 7), y la deja en resultado.txt. }
function Rechazar(const Texto: String): Boolean;
begin
  Log('No se puede usar: ' + Texto);
  if WizardSilent then
  begin
    if FalloSinPreguntar = '' then
      FalloSinPreguntar := Texto;
    Result := True;
  end
  else
  begin
    MsgBox(Texto, mbError, MB_OK);
    Result := False;
  end;
end;

{ Sí o no. En silencio vale Predeterminado (IDYES o IDNO). }
function Preguntar(const Texto: String; const Predeterminado: Integer): Boolean;
begin
  Log('Pregunta: ' + Texto);
  if WizardSilent then
    Result := Predeterminado = IDYES
  else
    Result := MsgBox(Texto, mbConfirmation, MB_YESNO) = IDYES;
end;

{ El valor de esa clave en un archivo «clave=valor» ya leído (el último, si se repite); vacío si no está. }
function Valor(const Lineas: TArrayOfString; const Clave: String): String;
var
  I, Igual: Integer;
begin
  Result := '';
  for I := GetArrayLength(Lineas) - 1 downto 0 do
  begin
    Igual := Pos('=', Lineas[I]);
    if (Igual > 1) and (CompareText(Copy(Lineas[I], 1, Igual - 1), Clave) = 0) then
    begin
      Result := Copy(Lineas[I], Igual + 1, Length(Lineas[I]));
      Exit;
    end;
  end;
end;

procedure Agregar(var Lineas: TArrayOfString; const Clave, Dato: String);
var
  N: Integer;
begin
  N := GetArrayLength(Lineas);
  SetArrayLength(Lineas, N + 1);
  Lineas[N] := Clave + '=' + Dato;
end;

{ /CLAVE=valor de la línea de órdenes; SinValor si no se dio. }
function Parametro(const Clave: String): String;
begin
  Result := ExpandConstant('{param:' + Clave + '|' + SinValor + '}');
end;

procedure Dividir(const Texto, Separador: String; var Partes: TArrayOfString);
var
  Resto: String;
  P, N: Integer;
begin
  SetArrayLength(Partes, 0);
  Resto := Texto;
  while Resto <> '' do
  begin
    P := Pos(Separador, Resto);
    N := GetArrayLength(Partes);
    SetArrayLength(Partes, N + 1);
    if P = 0 then
    begin
      Partes[N] := Resto;
      Resto := '';
    end
    else
    begin
      Partes[N] := Copy(Resto, 1, P - 1);
      Resto := Copy(Resto, P + Length(Separador), Length(Resto));
    end;
  end;
end;

function EmpiezaCon(const Texto, Inicio: String): Boolean;
begin
  Result := CompareText(Copy(Texto, 1, Length(Inicio)), Inicio) = 0;
end;

{ Sin los espacios de las orillas ni el punto del final: para seguir la frase con lo que dijo un script. }
function SinPunto(const Texto: String): String;
begin
  Result := Trim(Texto);
  if Copy(Result, Length(Result), 1) = '.' then
    Result := Copy(Result, 1, Length(Result) - 1);
end;

{ Un archivo «clave=valor» que no dejó el ayudante (los del paquete de mudanza: mudanza.txt y traida.txt): sin la marca de UTF-8 del
  principio, por si quedó pegada a su primer renglón (así no se reconocería su clave). Falso si no está o no se pudo leer. }
function LeerClaves(const Archivo: String; var Lineas: TArrayOfString): Boolean;
var
  Renglon: String;
begin
  SetArrayLength(Lineas, 0);
  Result := LoadStringsFromFile(Archivo, Lineas);
  if not Result then
    SetArrayLength(Lineas, 0)
  else if GetArrayLength(Lineas) > 0 then
  begin
    Renglon := Lineas[0];
    if Renglon <> '' then
      if Ord(Renglon[1]) = 65279 then
        Lineas[0] := Copy(Renglon, 2, Length(Renglon));
  end;
end;

function SoloCifras(const Texto: String): Boolean;
var
  I: Integer;
begin
  Result := Texto <> '';
  for I := 1 to Length(Texto) do
    if (Texto[I] < '0') or (Texto[I] > '9') then
      Result := False;
end;

{ Una IP o el nombre de una PC, con su puerto si acaso (de 1 a 65535): letras, números, «.», «-», «_» y un «:», sin empezar con «.» ni
  con «..» en medio. Lo mismo que deja pasar el ayudante (Respuestas.EsServidor). }
function EsDireccion(const Texto: String): Boolean;
var
  I, DosPuntos: Integer;
  C: Char;
  Puerto: String;
begin
  Result := Texto <> '';
  DosPuntos := 0;
  for I := 1 to Length(Texto) do
  begin
    C := Texto[I];
    if C = ':' then
    begin
      if DosPuntos > 0 then
        Result := False;
      DosPuntos := I;
    end
    else if not (((C >= '0') and (C <= '9')) or ((C >= 'a') and (C <= 'z')) or ((C >= 'A') and (C <= 'Z'))
      or (C = '.') or (C = '-') or (C = '_')) then
      Result := False;
  end;
  if (Copy(Texto, 1, 1) = '.') or (Pos('..', Texto) > 0) then
    Result := False;
  if Result and (DosPuntos > 0) then
  begin
    Puerto := Copy(Texto, DosPuntos + 1, Length(Texto));
    Result := (DosPuntos > 1) and SoloCifras(Puerto) and (Length(Puerto) <= 5) and (StrToIntDef(Puerto, 0) >= 1)
      and (StrToIntDef(Puerto, 0) <= 65535);
  end;
end;

{ La caja o el cajero de eleventa: vacío, o con cifras (hasta 9). 0 es «falta», como lo trae la plantilla. Lo mismo que el ayudante. }
function NumeroDeEleventa(const Texto: String): Boolean;
begin
  Result := (Texto = '') or (SoloCifras(Texto) and (Length(Texto) <= 9));
end;

{ Un 0 de eleventa (la caja, el cajero de la plantilla) es «falta»: la casilla empieza vacía. }
function SinCero(const Texto: String): String;
begin
  Result := Texto;
  if SoloCifras(Texto) and (StrToIntDef(Texto, -1) = 0) then
    Result := '';
end;

{ El código de Control Print como lo acepta: sin guiones ni espacios y en mayúsculas (como poner-codigo-controlprint.ps1). }
function CodigoNormal(const Texto: String): String;
var
  I: Integer;
  C: Char;
begin
  Result := '';
  for I := 1 to Length(Texto) do
  begin
    C := Texto[I];
    if ((C >= '0') and (C <= '9')) or ((C >= 'a') and (C <= 'z')) or ((C >= 'A') and (C <= 'Z')) then
      Result := Result + C;
  end;
  Result := Uppercase(Result);
end;

{ El primer número de Texto y Texto sin él: «1.0.57» da 1 y deja «0.57». }
function SacarNumero(var Texto: String): Integer;
var
  I: Integer;
  Cifras: String;
begin
  Cifras := '';
  I := 1;
  while I <= Length(Texto) do
  begin
    if (Texto[I] < '0') or (Texto[I] > '9') then
      Break;
    Cifras := Cifras + Texto[I];
    I := I + 1;
  end;
  Result := StrToIntDef(Cifras, 0);
  I := Pos('.', Texto);
  if I = 0 then
    Texto := ''
  else
    Texto := Copy(Texto, I + 1, Length(Texto));
end;

{ -1 si A es anterior a B, 0 si es la misma y 1 si es más nueva (como las compara actualizar.ps1). }
function CompararVersiones(const A, B: String): Integer;
var
  RestoA, RestoB: String;
  I, NumeroA, NumeroB: Integer;
begin
  RestoA := A;
  RestoB := B;
  Result := 0;
  for I := 1 to 4 do
  begin
    NumeroA := SacarNumero(RestoA);
    NumeroB := SacarNumero(RestoB);
    if NumeroA < NumeroB then
    begin
      Result := -1;
      Exit;
    end;
    if NumeroA > NumeroB then
    begin
      Result := 1;
      Exit;
    end;
  end;
end;

function Ayudante: String;
begin
  Result := ExpandConstant('{tmp}\PapeleriaPos.Instalacion.exe');
end;

function PowerShell: String;
begin
  Result := ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe');
end;

{ ── Las variables del servicio (la clave de eleventa, el código de Control Print) ── }

{ Las del servicio, como las guarda Windows (el valor Environment de su clave, REG_MULTI_SZ). Nunca se escriben en ningún registro. }
procedure LeerVariables(var Lista: TArrayOfString);
var
  Texto: String;
begin
  SetArrayLength(Lista, 0);
  if RegQueryMultiStringValue(HKLM, ClaveDelServicio, 'Environment', Texto) then
    Dividir(Texto, #0, Lista);
  Texto := '';
end;

{ Si el servicio ya tiene esa variable (sin leer su valor para nada más). }
function TieneVariable(const Nombre: String): Boolean;
var
  Lista: TArrayOfString;
  I: Integer;
begin
  Result := False;
  LeerVariables(Lista);
  for I := 0 to GetArrayLength(Lista) - 1 do
    if EmpiezaCon(Lista[I], Nombre + '=') and (Length(Lista[I]) > Length(Nombre) + 1) then
      Result := True;
  SetArrayLength(Lista, 0);
end;

{ Pone (o, con Dato vacío, quita) una variable del servicio sin tocar las demás, como poner-clave-eleventa.ps1: directo en el registro
  de Windows, sin pasarla por una línea de órdenes ni por un archivo. }
function PonerVariable(const Nombre, Dato: String): Boolean;
var
  Lista: TArrayOfString;
  Nuevo: String;
  I: Integer;
begin
  LeerVariables(Lista);
  Nuevo := '';
  for I := 0 to GetArrayLength(Lista) - 1 do
    if (Lista[I] <> '') and not EmpiezaCon(Lista[I], Nombre + '=') then
      Nuevo := Nuevo + Lista[I] + #0;
  if Dato <> '' then
    Nuevo := Nuevo + Nombre + '=' + Dato + #0;
  if Nuevo = '' then
  begin
    Result := True;
    if RegValueExists(HKLM, ClaveDelServicio, 'Environment') then
      Result := RegDeleteValue(HKLM, ClaveDelServicio, 'Environment');
  end
  else
    Result := RegWriteMultiStringValue(HKLM, ClaveDelServicio, 'Environment', Copy(Nuevo, 1, Length(Nuevo) - 1));
  Nuevo := '';
  SetArrayLength(Lista, 0);
end;

{ ── El ayudante ── }

{ PapeleriaPos.Instalacion.exe con esa orden; lo que dijo queda en Salida. Falso si no corrió o no dejó nada. }
function CorrerAyudante(const Orden, Archivo: String; var Salida: TArrayOfString): Boolean;
var
  Codigo: Integer;
begin
  Result := False;
  SetArrayLength(Salida, 0);
  DeleteFile(Archivo);
  if Exec(Ayudante, Orden + ' --salida "' + Archivo + '"', ExpandConstant('{tmp}'), SW_HIDE, ewWaitUntilTerminated, Codigo) then
    Result := LoadStringsFromFile(Archivo, Salida);
  Log('El ayudante (' + Orden + ') terminó con ' + IntToStr(Codigo));
end;

{ Si contesta la caja principal de esa dirección (su api/estado); Texto dice qué contestó o por qué no, y Aviso por qué esta caja
  todavía no se podría dar de alta con ella (es «La única caja», o no tiene tienda todavía). }
function ProbarPrincipal(const Direccion: String; var Texto, Aviso: String): Boolean;
var
  Salida: TArrayOfString;
begin
  Result := False;
  Texto := 'no se pudo probar';
  Aviso := '';
  if CorrerAyudante('probar --direccion "' + Direccion + '"', ExpandConstant('{tmp}\probar.txt'), Salida) then
  begin
    Result := Valor(Salida, 'contesta') = 'si';
    Texto := Valor(Salida, 'texto');
    if Texto = '' then
      Texto := Valor(Salida, 'error');
    Aviso := Valor(Salida, 'aviso');
  end;
end;

{ Si la caja de esta instalación está abierta (la de la prueba con una persona real, en PapeleriaPos-Prueba, no cuenta). }
function CajaAbierta: Boolean;
var
  Localizador, Servicio, Procesos, Proceso: Variant;
  I: Integer;
  Carpeta, Ruta: String;
begin
  Result := False;
  Carpeta := Lowercase(ExpandConstant('{commonpf64}\PapeleriaPos\Caja\'));
  try
    Localizador := CreateOleObject('WbemScripting.SWbemLocator');
    Servicio := Localizador.ConnectServer('.', 'root\CIMV2');
    Procesos := Servicio.ExecQuery('SELECT ExecutablePath FROM Win32_Process WHERE Name = ''PapeleriaPos.Caja.exe''');
    for I := 0 to Procesos.Count - 1 do
    begin
      Proceso := Procesos.ItemIndex(I);
      if VarIsNull(Proceso.ExecutablePath) then
        Result := True
      else
      begin
        { Lo que da WMI es un Variant: primero a texto, y luego a minúsculas. }
        Ruta := Proceso.ExecutablePath;
        Ruta := Lowercase(Ruta);
        if Pos(Carpeta, Ruta) = 1 then
          Result := True;
      end;
    end;
  except
    Log('No se pudo revisar si la caja está abierta: ' + GetExceptionMessage);
  end;
end;

{ ── Las páginas ── }

function Etiqueta(Pagina: TWizardPage; const Texto: String; Izquierda, Arriba, Ancho, Alto: Integer): TNewStaticText;
begin
  Result := TNewStaticText.Create(Pagina);
  Result.Parent := Pagina.Surface;
  Result.AutoSize := False;
  Result.WordWrap := True;
  Result.Caption := Texto;
  Result.Left := Izquierda;
  Result.Top := Arriba;
  Result.Width := Ancho;
  Result.Height := Alto;
end;

function Opcion(Pagina: TWizardPage; const Texto: String; Arriba: Integer): TNewRadioButton;
begin
  Result := TNewRadioButton.Create(Pagina);
  Result.Parent := Pagina.Surface;
  Result.Caption := Texto;
  Result.Left := 0;
  Result.Top := Arriba;
  Result.Width := Pagina.SurfaceWidth;
  Result.Height := ScaleY(17);
end;

function Campo(Pagina: TWizardPage; Izquierda, Arriba, Ancho: Integer): TNewEdit;
begin
  Result := TNewEdit.Create(Pagina);
  Result.Parent := Pagina.Surface;
  Result.Left := Izquierda;
  Result.Top := Arriba;
  Result.Width := Ancho;
end;

function CampoSecreto(Pagina: TWizardPage; Izquierda, Arriba, Ancho: Integer): TPasswordEdit;
begin
  Result := TPasswordEdit.Create(Pagina);
  Result.Parent := Pagina.Surface;
  Result.Left := Izquierda;
  Result.Top := Arriba;
  Result.Width := Ancho;
end;

function Desplegable(Pagina: TWizardPage; Izquierda, Arriba, Ancho: Integer; Escribible: Boolean): TNewComboBox;
begin
  Result := TNewComboBox.Create(Pagina);
  Result.Parent := Pagina.Surface;
  Result.Left := Izquierda;
  Result.Top := Arriba;
  Result.Width := Ancho;
  if Escribible then
    Result.Style := csDropDown
  else
    Result.Style := csDropDownList;
end;

function Boton(Pagina: TWizardPage; const Texto: String; Izquierda, Arriba, Ancho: Integer): TNewButton;
begin
  Result := TNewButton.Create(Pagina);
  Result.Parent := Pagina.Surface;
  Result.Caption := Texto;
  Result.Left := Izquierda;
  Result.Top := Arriba;
  Result.Width := Ancho;
  Result.Height := ScaleY(23);
end;

{ Lo que está marcado en «Qué es esta PC». Solo para esa página y para saber si sale la de la mudanza: para todo lo demás, RolElegido. }
function RolMarcado: String;
begin
  if RolOtra.Checked then
    Result := 'otra'
  else if RolUnica.Checked then
    Result := 'unica'
  else if RolPrincipal.Checked then
    Result := 'principal'
  else
    Result := '';
end;

{ Si la base de esta PC ya se sacó para mudarla a otra (la marca pos.db.mudada, que vio el ayudante): su servicio no arranca hasta que la
  mudanza se termine o se cancele, aquí mismo. }
function HayMarca: Boolean;
begin
  Result := Valor(Actual, 'mudanza') = 'sacada';
end;

{ Si esta PC ya tiene el servicio (el que lleva la base de la tienda). }
function TieneServicio: Boolean;
begin
  Result := Valor(Actual, 'servicio-instalado') = 'si';
end;

{ La mudanza que se pidió como /MUDANZA=sacar, traer, terminar o cancelar (en minúsculas); SinValor si no se dio. }
function MudanzaPedida: String;
begin
  Result := Lowercase(Trim(Parametro('MUDANZA')));
end;

{ Lo que se eligió en «La base de la tienda»: sacar, traer, terminar o cancelar; vacío, sin mudanza (también cuando esa página no sale: sin
  la marca y con «Otra caja»). Con la marca solo se termina (lo que viene elegido) o se cancela. Sacar, solo de una PC que tiene el servicio. }
function MudanzaElegida: String;
begin
  Result := '';
  if HayMarca then
  begin
    if Mudanza2.Checked then
      Result := 'cancelar'
    else
      Result := 'terminar';
  end
  else if (RolMarcado = 'principal') or (RolMarcado = 'unica') then
  begin
    if Mudanza3.Checked then
      Result := 'traer'
    else if Mudanza2.Checked and TieneServicio then
      Result := 'sacar';
  end;
end;

{ El rol que cuenta para todo: las páginas que salen, las respuestas, «Listo para instalar», «Cómo quedó» y la última página. Es el marcado
  en «Qué es esta PC», salvo con la base ya sacada: ahí viene marcada «Otra caja» sin poder cambiarse y decide la página siguiente. Al
  terminar la mudanza esta PC queda como «Otra caja»; al cancelarla, como lo que es hoy (lo que dijo el ayudante: principal o unica). }
function RolElegido: String;
begin
  if not HayMarca then
    Result := RolMarcado
  else if MudanzaElegida = 'cancelar' then
    Result := Valor(Actual, 'rol')
  else
    Result := 'otra';
end;

function ConServicio: Boolean;
begin
  Result := (RolElegido = 'principal') or (RolElegido = 'unica');
end;

{ La carpeta del paquete de mudanza como se escribió: sin comillas (de una ruta pegada) ni la diagonal del final, salvo en la raíz de una
  unidad («E:\»). }
function CarpetaDelPaquete: String;
begin
  Result := Trim(EditPaquete.Text);
  StringChangeEx(Result, '"', '', True);
  Result := RemoveBackslashUnlessRoot(Trim(Result));
end;

{ Una ruta completa de Windows: con su unidad («E:\mudanza») o de otra PC («\\PC\carpeta»). }
function EsRutaCompleta(const Ruta: String): Boolean;
begin
  Result := (Copy(Ruta, 2, 2) = ':\') or ((Copy(Ruta, 1, 2) = '\\') and (Length(Ruta) > 2));
end;

{ Una ruta para la línea de órdenes del ayudante: entre comillas y, si termina en diagonal (la raíz de una unidad), con esa diagonal
  repetida: una sola antes de la comilla la volvería parte del texto y se llevaría lo que sigue. Ahí solo van rutas: nunca una clave. }
function EntreComillas(const Ruta: String): String;
var
  I: Integer;
begin
  Result := Ruta;
  I := Length(Ruta);
  while I > 0 do
  begin
    if Ruta[I] <> '\' then
      Break;
    Result := Result + '\';
    I := I - 1;
  end;
  Result := '"' + Result + '"';
end;

{ Con qué paquete se llenaron las páginas (su carpeta y su identidad): para no volver a llenarlas con el mismo y pisar lo que ya se cambió. }
function IdDelPaquete: String;
begin
  Result := CarpetaDelPaquete + '|' + Valor(Paquete, 'paquete-id') + '|' + Valor(Paquete, 'hecho-local');
end;

{ El equipo de donde viene el paquete que se vio, para decirlo. }
function EquipoDelPaquete: String;
begin
  Result := Valor(Paquete, 'equipo');
  if Result = '' then
    Result := 'la otra PC';
end;

{ Si la base del paquete que se vio ya se puso en esta PC en una corrida anterior (la nota mudanza-puesta.txt, que dijo el ayudante): es un
  segundo intento con ese mismo paquete, para terminarlo. }
function PaqueteYaPuesto: Boolean;
begin
  Result := (Trim(Valor(Actual, 'mudanza-puesta')) <> '')
    and (CompareText(Trim(Valor(Actual, 'mudanza-puesta')), Trim(Valor(Paquete, 'paquete-id'))) = 0);
end;

{ Si con la base del paquete que se vio llega un puente con eleventa: allá se leía eleventa o se le escribía cada venta. Con cualquiera de los
  dos, como lo cuenta el ayudante al revisar y al escribir: «escribir» solo no es lo común, pero el servicio le escribe igual, y aquí llegaría
  escribiendo sin haberlo dicho. }
function PuenteDelPaquete: Boolean;
begin
  Result := (Valor(Paquete, 'eleventa') = 'si') or (Valor(Paquete, 'eleventa-escribir') = 'si');
end;

{ Cómo llega (o llegó) el puente con eleventa que viene con la base del paquete: escribiendo, con la caja de eleventa de cada caja que ya
  vendió, o sin escribir. Vacío si allá no había puente. }
function ComoLlegaElPuente(const Llega: String): String;
var
  I: Integer;
  Nombre, Numero, Cajas: String;
begin
  Result := '';
  if not PuenteDelPaquete then
    Exit;
  if Valor(Paquete, 'eleventa-escribir') <> 'si' then
  begin
    Result := 'El puente ' + Llega + ' SIN escribir: lo que se cobre en el POS no llega a eleventa hasta que tú lo prendas.';
    Exit;
  end;
  Cajas := '';
  for I := 1 to 50 do
  begin
    Nombre := Valor(Paquete, 'caja.' + IntToStr(I) + '.nombre');
    if Nombre = '' then
      Break;
    Numero := Valor(Paquete, 'caja.' + IntToStr(I) + '.eleventa-caja');
    { Solo las que ya tuvieron turnos quedan escritas con su caja de eleventa (Eleventa:Cajas). }
    if (Numero <> '') and (Valor(Paquete, 'caja.' + IntToStr(I) + '.con-turnos') <> 'no') then
    begin
      if Cajas <> '' then
        Cajas := Cajas + '; ';
      Cajas := Cajas + 'lo que cobre «' + Nombre + '» entra a eleventa como la caja ' + Numero;
    end;
  end;
  if Cajas = '' then
    Cajas := 'ninguna de las cajas que vienen tiene su caja de eleventa (lo suyo espera en la bandeja)';
  Result := 'El puente ' + Llega + ' ESCRIBIENDO en ' + Valor(Paquete, 'eleventa-base') + ': ' + Cajas + '.';
end;

{ Los avisos que trae el paquete que se vio (los escribió quien lo sacó), uno por renglón. }
function AvisosDelPaquete: String;
var
  I, N: Integer;
begin
  Result := '';
  N := StrToIntDef(Trim(Valor(Paquete, 'avisos')), 0);
  if N > 50 then
    N := 50;
  for I := 1 to N do
    if Valor(Paquete, 'aviso.' + IntToStr(I)) <> '' then
      Result := Result + '• ' + Valor(Paquete, 'aviso.' + IntToStr(I)) + #13#10;
end;

{ Las cajas que vienen en el paquete que se vio, para decir cuáles no hay que elegir en el alta de la PC nueva: «Caja 2» o «Caja 3». }
function CajasDelPaquete: String;
var
  I: Integer;
  Nombre: String;
begin
  Result := '';
  for I := 1 to 50 do
  begin
    Nombre := Valor(Paquete, 'caja.' + IntToStr(I) + '.nombre');
    if Nombre = '' then
      Break;
    if Result <> '' then
      Result := Result + ' o ';
    Result := Result + '«' + Nombre + '»';
  end;
end;

function NombreDelRol(const Rol: String): String;
begin
  if Rol = 'principal' then
    Result := 'Caja principal'
  else if Rol = 'unica' then
    Result := 'La única caja'
  else if Rol = 'otra' then
    Result := 'Otra caja'
  else
    Result := '(sin elegir)';
end;

{ La impresora de tickets como la lee la caja: vacía (en pantalla), «tcp://IP:9100» o el nombre de una impresora de Windows. }
function ValorTicket: String;
var
  Ip: String;
begin
  if ComboTicket.ItemIndex <= 0 then
    Result := ''
  else if ComboTicket.ItemIndex = 1 then
  begin
    Ip := Trim(EditTicketIp.Text);
    { La que ya tenía, como estaba escrita (con o sin el 9100): así, si no se cambia, caja.json no se toca. }
    if CompareText('tcp://' + Ip, Valor(Actual, 'ticket')) = 0 then
      Result := Valor(Actual, 'ticket')
    else
    begin
      if Pos(':', Ip) = 0 then
        Ip := Ip + ':9100';
      Result := 'tcp://' + Ip;
    end;
  end
  else
    Result := ComboTicket.Text;
end;

function ValorCola: String;
begin
  if ComboCola.ItemIndex <= 0 then
    Result := ''
  else
    Result := ComboCola.Text;
end;

procedure ActualizarImpresoras;
var
  I: Integer;
begin
  EditTicketIp.Visible := ComboTicket.ItemIndex = 1;
  EtiquetaTicketIp.Visible := EditTicketIp.Visible;
  I := ComboCola.ItemIndex - 1;
  if I < 0 then
    AvisoCola.Caption := ''
  else if I < GetArrayLength(ImpresorasAvisos) then
    AvisoCola.Caption := ImpresorasAvisos[I]
  else
    AvisoCola.Caption := 'No está entre las impresoras de esta PC: revisa que se llame así (como sale en Configuración › Impresoras).';
end;

procedure ActualizarLector;
begin
  ComboPuerto.Enabled := LectorCom.Checked;
  ComboBaudios.Enabled := LectorCom.Checked;
end;

procedure ActualizarControlPrint;
begin
  EditCpServidor.Enabled := CpSi.Checked;
  EditCodigo.Enabled := CpSi.Checked;
end;

procedure ActualizarEleventa;
begin
  { El puente ya prendido (el de esta PC, o el que llega con la base que se trae) se apaga con el instructivo, revisando antes lo pendiente:
    no desde aquí. }
  ElNo.Enabled := (Valor(Actual, 'eleventa-encendido') <> 'si') and not PuenteMudado;
  { Si ya le escribe, no se cambia a qué base desde aquí: un dedazo apuntaría las ventas a otra al reiniciar (el instructivo lo hace con su revisión). }
  EditElServidor.Enabled := ElSi.Checked and (Valor(Actual, 'eleventa-escribe') <> 'si');
  { Con la base que se trae llega su bandeja, anotada con la base de eleventa de allá: aquí cambia el servidor (pasa a ser esta PC), no la base. }
  EditElBase.Enabled := ElSi.Checked and (Valor(Actual, 'eleventa-escribe') <> 'si') and not PuenteMudado;
  EditElCaja.Enabled := ElSi.Checked;
  EditElCajero.Enabled := ElSi.Checked;
end;

procedure ActualizarGbak;
var
  Ruta, Version: String;
  I: Integer;
begin
  Ruta := Trim(ComboGbak.Text);
  if Ruta = '' then
  begin
    NotaGbak.Caption := 'Sin gbak no hay respaldo propio de eleventa, y sin ese respaldo la caja no le escribe. Si en esta PC no hay Firebird 2.0, '
      + 'se copia su carpeta bin de la caja principal (instructivo).';
    Exit;
  end;
  for I := 0 to GetArrayLength(GbakRutas) - 1 do
    if CompareText(GbakRutas[I], Ruta) = 0 then
    begin
      if GbakAvisos[I] = '' then
        NotaGbak.Caption := 'Es el de Firebird ' + GbakVersiones[I] + ': sirve.'
      else
        NotaGbak.Caption := GbakAvisos[I];
      Exit;
    end;
  if not FileExists(Ruta) then
    NotaGbak.Caption := 'No está ese archivo en esta PC.'
  else if GetVersionNumbersString(Ruta, Version) then
  begin
    if Copy(Version, 1, 4) = '2.0.' then
      NotaGbak.Caption := 'Es el de Firebird ' + Version + ': sirve.'
    else
      NotaGbak.Caption := 'Es de Firebird ' + Version + ': se necesita el de Firebird 2.0, como el de la tienda (nunca el de 2.5).';
  end
  else
    NotaGbak.Caption := 'No dice su versión: revisa que sea el de Firebird 2.0.';
end;

procedure AlCambiarImpresoras(Sender: TObject);
begin
  ActualizarImpresoras;
end;

procedure AlCambiarLector(Sender: TObject);
begin
  ActualizarLector;
end;

procedure AlCambiarControlPrint(Sender: TObject);
begin
  ActualizarControlPrint;
end;

procedure AlCambiarEleventa(Sender: TObject);
begin
  ActualizarEleventa;
end;

procedure AlCambiarGbak(Sender: TObject);
begin
  ActualizarGbak;
end;

procedure AlBuscar(Sender: TObject);
var
  Salida: TArrayOfString;
  I, N: Integer;
begin
  BotonBuscar.Enabled := False;
  BotonProbar.Enabled := False;
  EstadoPrincipal.Caption := 'Buscando en la red…';
  ListaEncontradas.Items.Clear;
  try
    if not CorrerAyudante('buscar', ExpandConstant('{tmp}\buscar.txt'), Salida) then
    begin
      EstadoPrincipal.Caption := 'No se pudo buscar en la red: escribe su IP.';
      Exit;
    end;
    N := StrToIntDef(Valor(Salida, 'encontradas'), 0);
    SetArrayLength(Encontradas, N);
    SetArrayLength(EncontradasAvisos, N);
    for I := 1 to N do
    begin
      Encontradas[I - 1] := Valor(Salida, 'encontrada.' + IntToStr(I));
      EncontradasAvisos[I - 1] := Valor(Salida, 'encontrada.' + IntToStr(I) + '.aviso');
      ListaEncontradas.Items.Add(Valor(Salida, 'encontrada.' + IntToStr(I) + '.texto'));
    end;
    if N = 0 then
      EstadoPrincipal.Caption := 'No contestó ninguna caja principal. Revisa que esté prendida y en la misma red, o escribe su IP.'
    else if N = 1 then
    begin
      ListaEncontradas.ItemIndex := 0;
      EditPrincipal.Text := Encontradas[0];
      EstadoPrincipal.Caption := 'Se encontró: ' + ListaEncontradas.Items.Strings[0];
      if EncontradasAvisos[0] <> '' then
        EstadoPrincipal.Caption := EstadoPrincipal.Caption + #13#10 + 'Ojo: ' + EncontradasAvisos[0];
    end
    else
      EstadoPrincipal.Caption := 'Se encontraron ' + IntToStr(N) + ': elige la de esta tienda.';
  finally
    BotonBuscar.Enabled := True;
    BotonProbar.Enabled := True;
  end;
end;

procedure AlElegirEncontrada(Sender: TObject);
var
  I: Integer;
begin
  I := ListaEncontradas.ItemIndex;
  if (I >= 0) and (I < GetArrayLength(Encontradas)) then
  begin
    EditPrincipal.Text := Encontradas[I];
    EstadoPrincipal.Caption := 'Elegida: ' + ListaEncontradas.Items.Strings[I];
    if (I < GetArrayLength(EncontradasAvisos)) and (EncontradasAvisos[I] <> '') then
      EstadoPrincipal.Caption := EstadoPrincipal.Caption + #13#10 + 'Ojo: ' + EncontradasAvisos[I];
  end;
end;

procedure AlProbar(Sender: TObject);
var
  Texto, Aviso: String;
begin
  if not EsDireccion(Trim(EditPrincipal.Text)) then
  begin
    EstadoPrincipal.Caption := 'Escribe la IP de la caja principal (por ejemplo, 192.168.1.10).';
    Exit;
  end;
  BotonProbar.Enabled := False;
  EstadoPrincipal.Caption := 'Probando…';
  try
    if ProbarPrincipal(Trim(EditPrincipal.Text), Texto, Aviso) then
    begin
      EstadoPrincipal.Caption := 'Contesta: ' + Texto;
      if Aviso <> '' then
        EstadoPrincipal.Caption := EstadoPrincipal.Caption + #13#10 + 'Ojo: ' + Aviso;
    end
    else
      EstadoPrincipal.Caption := 'No contestó: ' + Texto;
  finally
    BotonProbar.Enabled := True;
  end;
end;

procedure AlBuscarGbak(Sender: TObject);
var
  Ruta: String;
begin
  Ruta := Trim(ComboGbak.Text);
  if GetOpenFileName('El gbak.exe de Firebird 2.0', Ruta, ExtractFilePath(Ruta), 'gbak.exe|gbak.exe|Programas (*.exe)|*.exe', 'exe') then
  begin
    ComboGbak.Text := Ruta;
    ActualizarGbak;
  end;
end;

procedure AlBuscarRespaldos(Sender: TObject);
var
  Carpeta: String;
begin
  Carpeta := Trim(EditRespaldos.Text);
  if BrowseForFolder('La carpeta de los respaldos de eleventa (en esta PC, nunca en OneDrive):', Carpeta, True) then
    EditRespaldos.Text := Carpeta;
end;

{ Lo que dice el ayudante del paquete de mudanza de la carpeta escrita (mudanza-ver, que no cambia nada): si se puede traer y de qué PC es.
  Queda en Paquete, con la carpeta en PaqueteVisto. En su línea de órdenes solo va la carpeta. }
procedure VerElPaquete;
var
  Carpeta: String;
begin
  if ViendoElPaquete then
    Exit;
  Carpeta := CarpetaDelPaquete;
  SetArrayLength(Paquete, 0);
  PaqueteVisto := '';
  if Carpeta = '' then
    Exit;
  ViendoElPaquete := True;
  NotaMudanza.Caption := 'Revisando el paquete de mudanza de esa carpeta…';
  try
    if not CorrerAyudante('mudanza-ver --paquete ' + EntreComillas(Carpeta) + ' --version "' + VersionDelInstalador + '"',
      ExpandConstant('{tmp}\mudanza-ver.txt'), Paquete) then
    begin
      SetArrayLength(Paquete, 0);
      Agregar(Paquete, 'paquete', 'no-se');
      Agregar(Paquete, 'error', 'No se pudo revisar esa carpeta (el ayudante del instalador no corrió): revisa que el antivirus no lo esté deteniendo.');
    end;
    PaqueteVisto := Carpeta;
  finally
    ViendoElPaquete := False;
  end;
end;

{ Si lo que hay en Paquete es de la carpeta que está escrita ahora. }
function PaqueteAlDia: Boolean;
begin
  Result := (PaqueteVisto <> '') and (CompareText(PaqueteVisto, CarpetaDelPaquete) = 0);
end;

{ Lo que se enseña del paquete de la carpeta escrita: su resumen (lo arma el ayudante) o por qué no se puede traer. }
function TextoDelPaquete: String;
begin
  if CarpetaDelPaquete = '' then
    Result := 'Escribe la carpeta del paquete de mudanza, o elígela con «Buscar…»: por lo común, la carpeta «mudanza» de la USB que lleva este instalador.'
  else if not PaqueteAlDia then
    Result := 'Al dar «Siguiente» se revisa el paquete de mudanza de esa carpeta.'
  else if Valor(Paquete, 'paquete') = 'bien' then
  begin
    Result := Valor(Paquete, 'texto');
    if Result = '' then
      Result := 'En esa carpeta está, completo, el paquete de mudanza de ' + EquipoDelPaquete + '.';
  end
  else
  begin
    Result := Valor(Paquete, 'error');
    if Result = '' then
      Result := 'En esa carpeta no hay un paquete de mudanza que se pueda traer.';
  end;
end;

{ «La base de la tienda» según lo elegido: la carpeta solo sirve para sacar la base (dónde queda el paquete) y para traerla (dónde está), y
  la nota dice qué va a pasar. Con la base ya sacada no hay carpeta que elegir y su nota no cambia (PrepararLaMudanza). }
procedure ActualizarMudanza;
var
  Texto: String;
begin
  if HayMarca then
    Exit;
  EditPaquete.Enabled := Mudanza2.Checked or Mudanza3.Checked;
  BotonPaquete.Enabled := EditPaquete.Enabled;
  EtiquetaPaquete.Enabled := EditPaquete.Enabled;
  if Mudanza3.Checked then
    Texto := TextoDelPaquete
  else if Mudanza2.Checked then
    Texto := 'Al instalar, esta PC deja de atender (su servicio queda detenido a propósito) y su base queda en el paquete de mudanza, en esa '
      + 'carpeta. Después se lleva la USB a la otra PC, se corre ahí este instalador («Caja principal») y se corre otra vez aquí, para terminar.'
      + #13#10#13#10 + 'Antes, cierra la caja. No hagas el corte en eleventa hasta terminar. Si te arrepientes, este instalador ofrece aquí '
      + '«Cancelar la mudanza».'
  else
  begin
    if TieneServicio then
      Texto := 'Sin mudanza: esta PC sigue con su base, como hoy.'
    else if Valor(Actual, 'base') = 'si' then
      Texto := 'Ojo: en esta PC quedó una base de antes (C:\ProgramData\PapeleriaPos\pos.db). Sin mudanza, el servicio arranca con ella; con «Traer '
        + 'la base de otra PC» se aparta (no se borra) y se pone la del paquete.'
    else
      Texto := 'Sin mudanza: esta PC empieza con la base vacía, y la tienda se pone al abrir la caja.';
    { Si en esa carpeta hay un paquete que se puede traer, que se sepa: en una PC que ya tiene el servicio no viene elegido solo (salvo que ya
      se le haya puesto esa misma base), y es lo que se busca al repetir después de un intento que falló. No si es el que sacó esta misma PC
      (una mudanza que se canceló sin la USB puesta): ese no es para traerlo aquí. }
    if PaqueteAlDia and (Valor(Paquete, 'paquete') = 'bien') and (CompareText(Trim(Valor(Paquete, 'equipo')), GetComputerNameString) <> 0) then
      Texto := Texto + #13#10#13#10 + 'En ' + CarpetaDelPaquete + ' hay un paquete de mudanza, el de ' + EquipoDelPaquete + ': para poner aquí esa base, '
        + 'elige «Traer la base de otra PC».';
  end;
  NotaMudanza.Caption := Texto;
end;

procedure AlCambiarMudanza(Sender: TObject);
begin
  { Al elegir «Traer», el resumen del paquete de la carpeta escrita, si todavía no se ha visto. }
  if not HayMarca then
    if Mudanza3.Checked and (CarpetaDelPaquete <> '') and not PaqueteAlDia then
      VerElPaquete;
  ActualizarMudanza;
end;

procedure AlCambiarPaquete(Sender: TObject);
begin
  ActualizarMudanza;
end;

procedure AlBuscarPaquete(Sender: TObject);
var
  Carpeta: String;
begin
  Carpeta := CarpetaDelPaquete;
  if BrowseForFolder('La carpeta del paquete de mudanza (por lo común, la carpeta «mudanza» de la USB que lleva este instalador):', Carpeta, True) then
  begin
    EditPaquete.Text := Carpeta;
    if Mudanza3.Checked then
      VerElPaquete;
    ActualizarMudanza;
  end;
end;

procedure CrearPaginas;
var
  Ancho: Integer;
begin
  { Qué es esta PC. }
  PaginaRol := CreateCustomPage(wpWelcome, 'Qué es esta PC', 'Elige qué hará esta PC en la tienda: de eso depende lo que se instala.');
  Ancho := PaginaRol.SurfaceWidth;
  RolPrincipal := Opcion(PaginaRol, 'Caja principal', 0);
  RolPrincipal.Font.Style := [fsBold];
  Etiqueta(PaginaRol, 'Aquí vive la base de la tienda: las otras cajas y los celulares se conectan a esta PC.', ScaleX(18), ScaleY(19), Ancho - ScaleX(18), ScaleY(28));
  RolOtra := Opcion(PaginaRol, 'Otra caja', ScaleY(52));
  RolOtra.Font.Style := [fsBold];
  Etiqueta(PaginaRol, 'Una caja más de la tienda: se conecta a la caja principal, que ya está instalada en otra PC.', ScaleX(18), ScaleY(71), Ancho - ScaleX(18), ScaleY(28));
  RolUnica := Opcion(PaginaRol, 'La única caja', ScaleY(104));
  RolUnica.Font.Style := [fsBold];
  Etiqueta(PaginaRol, 'La tienda tiene solo esta caja: aquí vive la base; los celulares sí se conectan, otra caja no.', ScaleX(18), ScaleY(123), Ancho - ScaleX(18), ScaleY(28));
  NotaRol := Etiqueta(PaginaRol, '', 0, ScaleY(160), Ancho, ScaleY(64));

  { La base de la tienda: si se queda donde está o se muda de una PC a otra con el paquete de mudanza (con servicio, o con la base ya
    sacada). Lo que dice cada opción depende de lo que tiene esta PC: lo pone PrepararLaMudanza. Los avisos de sus controles se conectan
    al final, ya con todos creados. }
  PaginaMudanza := CreateCustomPage(PaginaRol.ID, 'La base de la tienda', 'Si la base de la tienda se queda donde está, o se muda de una PC a otra con el paquete de mudanza.');
  Ancho := PaginaMudanza.SurfaceWidth;
  Mudanza1 := Opcion(PaginaMudanza, '', 0);
  Mudanza2 := Opcion(PaginaMudanza, '', ScaleY(21));
  Mudanza3 := Opcion(PaginaMudanza, '', ScaleY(42));
  EtiquetaPaquete := Etiqueta(PaginaMudanza, 'La carpeta del paquete de mudanza (por lo común, la carpeta «mudanza» de la USB):', ScaleX(18), ScaleY(66),
    Ancho - ScaleX(18), ScaleY(16));
  EditPaquete := Campo(PaginaMudanza, ScaleX(18), ScaleY(84), Ancho - ScaleX(108));
  BotonPaquete := Boton(PaginaMudanza, 'Buscar…', Ancho - ScaleX(80), ScaleY(83), ScaleX(80));
  NotaMudanza := Etiqueta(PaginaMudanza, '', 0, ScaleY(114), Ancho, ScaleY(118));
  Mudanza1.OnClick := @AlCambiarMudanza;
  Mudanza2.OnClick := @AlCambiarMudanza;
  Mudanza3.OnClick := @AlCambiarMudanza;
  EditPaquete.OnChange := @AlCambiarPaquete;
  BotonPaquete.OnClick := @AlBuscarPaquete;

  { Dónde está la caja principal (solo «Otra caja»). }
  PaginaPrincipal := CreateCustomPage(PaginaMudanza.ID, 'Dónde está la caja principal', 'Esta caja se conecta a la caja principal de la tienda.');
  Ancho := PaginaPrincipal.SurfaceWidth;
  EtiquetaPrincipal := Etiqueta(PaginaPrincipal, DondeEstaLaPrincipal, 0, 0, Ancho, ScaleY(30));
  EditPrincipal := Campo(PaginaPrincipal, 0, ScaleY(36), ScaleX(160));
  BotonBuscar := Boton(PaginaPrincipal, 'Buscar en la red', ScaleX(170), ScaleY(35), ScaleX(130));
  BotonBuscar.OnClick := @AlBuscar;
  BotonProbar := Boton(PaginaPrincipal, 'Probar', ScaleX(306), ScaleY(35), ScaleX(80));
  BotonProbar.OnClick := @AlProbar;
  ListaEncontradas := TNewListBox.Create(PaginaPrincipal);
  ListaEncontradas.Parent := PaginaPrincipal.Surface;
  ListaEncontradas.Left := 0;
  ListaEncontradas.Top := ScaleY(68);
  ListaEncontradas.Width := Ancho;
  ListaEncontradas.Height := ScaleY(64);
  ListaEncontradas.OnClick := @AlElegirEncontrada;
  EstadoPrincipal := Etiqueta(PaginaPrincipal, '', 0, ScaleY(140), Ancho, ScaleY(90));

  { Las impresoras de esta PC. }
  PaginaImpresoras := CreateCustomPage(PaginaPrincipal.ID, 'Las impresoras de esta PC', 'Dónde sale el ticket, y qué impresora mide la caja cuando se imprime desde aquí.');
  Ancho := PaginaImpresoras.SurfaceWidth;
  Etiqueta(PaginaImpresoras, 'El ticket:', 0, 0, Ancho, ScaleY(16));
  ComboTicket := Desplegable(PaginaImpresoras, 0, ScaleY(18), ScaleX(250), False);
  ComboTicket.OnChange := @AlCambiarImpresoras;
  EtiquetaTicketIp := Etiqueta(PaginaImpresoras, 'Su IP:', ScaleX(260), ScaleY(21), ScaleX(40), ScaleY(16));
  EditTicketIp := Campo(PaginaImpresoras, ScaleX(300), ScaleY(18), ScaleX(130));
  Etiqueta(PaginaImpresoras, 'Lo recomendado es la impresora de tickets por su IP (tcp://IP:9100): así la usan las dos cajas sin depender de otra PC.',
    0, ScaleY(46), Ancho, ScaleY(30));
  Etiqueta(PaginaImpresoras, 'La impresora que mide la caja (la de inyección, cuando se imprime desde esta PC):', 0, ScaleY(84), Ancho, ScaleY(16));
  ComboCola := Desplegable(PaginaImpresoras, 0, ScaleY(102), ScaleX(300), False);
  ComboCola.OnChange := @AlCambiarImpresoras;
  Etiqueta(PaginaImpresoras, 'A esa se le activa «Conservar documentos impresos»: la caja mide cada trabajo y después lo quita de la cola.',
    0, ScaleY(130), Ancho, ScaleY(30));
  AvisoCola := Etiqueta(PaginaImpresoras, '', 0, ScaleY(166), Ancho, ScaleY(50));
  AvisoCola.Font.Color := ColorDeAviso;

  { El lector de códigos. }
  PaginaLector := CreateCustomPage(PaginaImpresoras.ID, 'El lector de códigos', 'Cómo le llega a la caja lo que escanea el lector.');
  Ancho := PaginaLector.SurfaceWidth;
  LectorTeclado := Opcion(PaginaLector, 'Como teclado (lo normal)', 0);
  LectorTeclado.OnClick := @AlCambiarLector;
  Etiqueta(PaginaLector, 'El lector escribe el código como si se tecleara: no hay que configurar nada.', ScaleX(18), ScaleY(19), Ancho - ScaleX(18), ScaleY(28));
  LectorCom := Opcion(PaginaLector, 'Por puerto COM (como lo tiene eleventa en esta PC)', ScaleY(52));
  LectorCom.OnClick := @AlCambiarLector;
  Etiqueta(PaginaLector, 'Puerto:', ScaleX(18), ScaleY(77), ScaleX(50), ScaleY(16));
  ComboPuerto := Desplegable(PaginaLector, ScaleX(70), ScaleY(74), ScaleX(90), True);
  Etiqueta(PaginaLector, 'Velocidad:', ScaleX(180), ScaleY(77), ScaleX(65), ScaleY(16));
  ComboBaudios := Desplegable(PaginaLector, ScaleX(250), ScaleY(74), ScaleX(90), True);
  ComboBaudios.Items.Add('1200');
  ComboBaudios.Items.Add('2400');
  ComboBaudios.Items.Add('4800');
  ComboBaudios.Items.Add('9600');
  ComboBaudios.Items.Add('19200');
  ComboBaudios.Items.Add('38400');
  ComboBaudios.Items.Add('57600');
  ComboBaudios.Items.Add('115200');
  NotaLector := Etiqueta(PaginaLector, '', ScaleX(18), ScaleY(106), Ancho - ScaleX(18), ScaleY(60));

  { Control Print (con servicio). }
  PaginaControlPrint := CreateCustomPage(PaginaLector.ID, 'Control Print', 'Si la tienda cobra las impresiones con Control Print: la caja enseña su cola por cobrar, solo leyéndola.');
  Ancho := PaginaControlPrint.SurfaceWidth;
  CpNo := Opcion(PaginaControlPrint, 'No: la tienda no usa Control Print', 0);
  CpNo.OnClick := @AlCambiarControlPrint;
  CpSi := Opcion(PaginaControlPrint, 'Sí: la caja enseña lo que está por cobrar en Control Print', ScaleY(20));
  CpSi.OnClick := @AlCambiarControlPrint;
  Etiqueta(PaginaControlPrint, 'La IP de la PC de Control Print (la caja principal de la tienda):', 0, ScaleY(48), Ancho, ScaleY(16));
  EditCpServidor := Campo(PaginaControlPrint, 0, ScaleY(66), ScaleX(160));
  Etiqueta(PaginaControlPrint, 'Su código de conexión (en esa PC: Control Print → Ajustes → Red, «Código de conexión»):', 0, ScaleY(96), Ancho, ScaleY(30));
  EditCodigo := CampoSecreto(PaginaControlPrint, 0, ScaleY(128), ScaleX(200));
  NotaCodigo := Etiqueta(PaginaControlPrint, '', 0, ScaleY(156), Ancho, ScaleY(64));

  { eleventa (con servicio). }
  PaginaEleventa := CreateCustomPage(PaginaControlPrint.ID, 'eleventa', 'Si esta caja convive con eleventa: la caja lee su catálogo y sus existencias.');
  Ancho := PaginaEleventa.SurfaceWidth;
  ElNo := Opcion(PaginaEleventa, 'No: esta caja no convive con eleventa', 0);
  ElNo.OnClick := @AlCambiarEleventa;
  ElSi := Opcion(PaginaEleventa, 'Sí: la caja lee el catálogo y las existencias de eleventa', ScaleY(20));
  ElSi.OnClick := @AlCambiarEleventa;
  Etiqueta(PaginaEleventa, 'La IP de la PC de eleventa (su servidor):', 0, ScaleY(49), ScaleX(215), ScaleY(16));
  EditElServidor := Campo(PaginaEleventa, ScaleX(220), ScaleY(46), ScaleX(150));
  Etiqueta(PaginaEleventa, 'La base de eleventa en esa PC:', 0, ScaleY(74), Ancho, ScaleY(16));
  EditElBase := Campo(PaginaEleventa, 0, ScaleY(92), Ancho);
  Etiqueta(PaginaEleventa, 'El número de esta caja en eleventa:', 0, ScaleY(123), ScaleX(190), ScaleY(16));
  EditElCaja := Campo(PaginaEleventa, ScaleX(195), ScaleY(120), ScaleX(45));
  Etiqueta(PaginaEleventa, 'el del cajero:', ScaleX(255), ScaleY(123), ScaleX(75), ScaleY(16));
  EditElCajero := Campo(PaginaEleventa, ScaleX(335), ScaleY(120), ScaleX(45));
  { Hasta abajo: al traer una base dice además cómo llega el puente, caja por caja. }
  NotaEleventa := Etiqueta(PaginaEleventa, '', 0, ScaleY(150), Ancho, PaginaEleventa.SurfaceHeight - ScaleY(150));

  PaginaEleventa2 := CreateCustomPage(PaginaEleventa.ID, 'eleventa: respaldos y clave', 'Con qué se respalda eleventa antes de escribirle, y la clave de su Firebird.');
  Ancho := PaginaEleventa2.SurfaceWidth;
  Etiqueta(PaginaEleventa2, 'El gbak.exe de Firebird 2.0 (con él se respalda eleventa cada día antes de escribirle; nunca el de 2.5):', 0, 0, Ancho, ScaleY(30));
  ComboGbak := Desplegable(PaginaEleventa2, 0, ScaleY(32), Ancho - ScaleX(90), True);
  ComboGbak.OnChange := @AlCambiarGbak;
  BotonGbak := Boton(PaginaEleventa2, 'Buscar…', Ancho - ScaleX(80), ScaleY(31), ScaleX(80));
  BotonGbak.OnClick := @AlBuscarGbak;
  NotaGbak := Etiqueta(PaginaEleventa2, '', 0, ScaleY(58), Ancho, ScaleY(30));
  Etiqueta(PaginaEleventa2, 'Dónde van los respaldos de eleventa (en esta PC, nunca en OneDrive; vacía, junto a la base):', 0, ScaleY(92), Ancho, ScaleY(16));
  EditRespaldos := Campo(PaginaEleventa2, 0, ScaleY(110), Ancho - ScaleX(90));
  BotonRespaldos := Boton(PaginaEleventa2, 'Buscar…', Ancho - ScaleX(80), ScaleY(109), ScaleX(80));
  BotonRespaldos.OnClick := @AlBuscarRespaldos;
  Etiqueta(PaginaEleventa2, 'La clave de Firebird de eleventa (la de SYSDBA):', 0, ScaleY(142), Ancho, ScaleY(16));
  EditClave := CampoSecreto(PaginaEleventa2, 0, ScaleY(160), ScaleX(200));
  NotaClave := Etiqueta(PaginaEleventa2, '', 0, ScaleY(188), Ancho, ScaleY(44));

  { Cómo quedó (después de instalar). }
  PaginaResultado := CreateCustomPage(wpInstalling, 'Cómo quedó', 'Lo que hizo el instalador y lo que contestó cada parte.');
  MemoResultado := TNewMemo.Create(PaginaResultado);
  MemoResultado.Parent := PaginaResultado.Surface;
  MemoResultado.Left := 0;
  MemoResultado.Top := 0;
  MemoResultado.Width := PaginaResultado.SurfaceWidth;
  MemoResultado.Height := PaginaResultado.SurfaceHeight;
  MemoResultado.ReadOnly := True;
  MemoResultado.ScrollBars := ssVertical;
end;

{ Control Print y eleventa como los tiene esta PC (lo que dijo el ayudante). Aparte de Prellenar porque se repite si se deja de traer una base:
  esas dos páginas vuelven a lo de esta PC. }
procedure PrellenarElPuente;
begin
  PaqueteAplicado := '';
  PuenteMudado := False;
  PuenteMudadoEscribe := False;
  CpSi.Checked := Valor(Actual, 'controlprint') = 'si';
  CpNo.Checked := not CpSi.Checked;
  EditCpServidor.Text := Valor(Actual, 'controlprint-servidor');

  ElSi.Checked := Valor(Actual, 'eleventa') = 'si';
  ElNo.Checked := not ElSi.Checked;
  EditElServidor.Text := Valor(Actual, 'eleventa-servidor');
  EditElBase.Text := Valor(Actual, 'eleventa-base');
  EditElCaja.Text := SinCero(Valor(Actual, 'eleventa-caja'));
  EditElCajero.Text := SinCero(Valor(Actual, 'eleventa-cajero'));
  NotaEleventa.Caption := 'La caja solo lee eleventa: escribirle cada venta lo empieza el dueño después, con el instructivo, y mientras tanto no se '
    + 'vende en la caja nueva. La base de la caja no se arma aquí: para tener la de eleventa con sus usuarios, importar-eleventa, como dice el '
    + 'instructivo; si no, al abrir la caja se crea la tienda y la lectura trae el catálogo.';
  if Valor(Actual, 'eleventa-encendido') = 'si' then
  begin
    { El puente ya está prendido: se apaga con el instructivo (revisando antes lo pendiente), no desde aquí (ActualizarEleventa apaga el «No»). }
    NotaEleventa.Caption := 'Esta caja ya lee eleventa';
    if Valor(Actual, 'eleventa-escribe') = 'si' then
      NotaEleventa.Caption := NotaEleventa.Caption + ' y le escribe cada venta (eso se queda como está)';
    NotaEleventa.Caption := NotaEleventa.Caption + '. Para apagar el puente: el instructivo, «Regresar o terminar el piloto», revisando antes lo '
      + 'pendiente.';
    if Valor(Actual, 'eleventa-escribe') <> 'si' then
      NotaEleventa.Caption := NotaEleventa.Caption + ' La caja no le escribe a eleventa hasta que el dueño lo empiece con el instructivo.';
  end
  else if Valor(Actual, 'eleventa-servidor-del-ini') = 'si' then
    NotaEleventa.Caption := 'La IP es la que tiene el eleventa de esta PC (su pdventa.ini). ' + NotaEleventa.Caption;
end;

{ Al traer una base, Control Print y eleventa empiezan con lo de la PC de donde viene, pasado a esta (lo que propone el ayudante con su
  paquete): el puente llega como estaba allá, y si allá estaba prendido aquí no se apaga ni se le cambia la base. El número de esta PC en
  eleventa empieza vacío: el de allá es de la otra caja, que queda escrita aparte, por su serie. El gbak y la carpeta de los respaldos son
  los de esta PC. Nunca las claves: esas se escriben aquí, en sus casillas. }
procedure PonerLoDelPaquete;
begin
  PrellenarElPuente;
  if Valor(Paquete, 'controlprint') = 'si' then
  begin
    CpSi.Checked := True;
    CpNo.Checked := False;
    if Valor(Paquete, 'propone-controlprint-servidor') <> '' then
      EditCpServidor.Text := Valor(Paquete, 'propone-controlprint-servidor');
  end;
  if PuenteDelPaquete then
  begin
    PuenteMudado := True;
    PuenteMudadoEscribe := Valor(Paquete, 'eleventa-escribir') = 'si';
    ElSi.Checked := True;
    ElNo.Checked := False;
    if Valor(Paquete, 'propone-eleventa-servidor') <> '' then
      EditElServidor.Text := Valor(Paquete, 'propone-eleventa-servidor');
    if Valor(Paquete, 'eleventa-base') <> '' then
      EditElBase.Text := Valor(Paquete, 'eleventa-base');
    { En un segundo intento con este mismo paquete (su base ya se puso aquí), el número de esta PC y el cajero se quedan como los tiene esta PC,
      que es lo que se contestó la primera vez: vaciar el número lo quitaría de servicio.json sin avisar. El de allá nunca llega a ser el de
      esta PC, así que el que tenga es suyo. }
    if not PaqueteYaPuesto then
    begin
      EditElCaja.Text := '';
      EditElCajero.Text := SinCero(Valor(Paquete, 'eleventa-cajero'));
    end;
    NotaEleventa.Caption := ComoLlegaElPuente('llega') + #13#10 + 'El número de esta PC en eleventa. Si no lo sabes, déjalo vacío: no afecta a la '
      + 'otra caja; se pone después corriendo el instalador otra vez.';
  end;
  PaqueteAplicado := IdDelPaquete;
end;

{ «La base de la tienda» según lo que tiene esta PC. Con la base ya sacada (la marca): terminar la mudanza, que viene elegido, o cancelarla.
  Sin la marca: sin mudanza (lo que viene elegido), sacar la base (solo si esta PC tiene el servicio) o traer la de otra PC, que se ofrece
  siempre: así un segundo intento, con el programa ya instalado, también se puede elegir. Si se puede, lo dice la revisión, no esta página. }
procedure PrepararLaMudanza;
var
  Texto: String;
begin
  EditPaquete.Text := ExpandConstant('{src}\mudanza');
  if HayMarca then
  begin
    Mudanza1.Caption := 'Terminar la mudanza: esta PC queda como «Otra caja»';
    Mudanza2.Caption := 'Cancelar la mudanza: esta PC vuelve a tener la base';
    Mudanza3.Visible := False;
    EtiquetaPaquete.Visible := False;
    EditPaquete.Visible := False;
    BotonPaquete.Visible := False;
    NotaMudanza.Top := ScaleY(50);
    Texto := 'La base de esta PC se sacó para mudarla';
    if Valor(Actual, 'mudanza-cuando') <> '' then
      Texto := Texto + ' (' + Valor(Actual, 'mudanza-cuando') + ')';
    if Valor(Actual, 'mudanza-carpeta') <> '' then
      Texto := Texto + '; su paquete quedó en ' + Valor(Actual, 'mudanza-carpeta');
    NotaMudanza.Caption := Texto + '.' + #13#10#13#10
      + 'Terminar: cuando la otra PC ya tiene la base (allá ya terminó este instalador). Esta PC deja su servicio, aparta su base (no la borra) y '
      + 'queda como «Otra caja» de esa PC, con su misma alta. Conviene tener puesta la USB de la mudanza: trae la dirección de esa PC.' + #13#10#13#10
      + 'Cancelar: esta PC vuelve a trabajar con su base, como antes. Si la otra PC ya tiene la base, primero se desinstala allá con «Borrar '
      + 'también los datos»; si la USB dice que ya se trajo, el instalador pregunta antes de cancelar.';
  end
  else
  begin
    if TieneServicio then
      Mudanza1.Caption := 'La base de la tienda se queda en esta PC'
    else
      Mudanza1.Caption := 'Empezar sin base (tienda nueva)';
    Mudanza2.Caption := 'Mudar la base de esta PC a otra PC (sacar el paquete de mudanza)';
    Mudanza2.Visible := TieneServicio;
    Mudanza3.Caption := 'Traer la base de otra PC (paquete de mudanza)';
    if not TieneServicio then
      Mudanza3.Top := Mudanza2.Top;
  end;
  NotaMudanza.Height := PaginaMudanza.SurfaceHeight - NotaMudanza.Top;
  Mudanza1.Checked := True;
  ActualizarMudanza;
end;

{ Lo que esta PC ya tiene: así cada página empieza con eso (nunca las claves: solo si el servicio ya tiene una). }
procedure Prellenar;
var
  I, N: Integer;
  Texto: String;
  Puertos: TArrayOfString;
begin
  Texto := Valor(Actual, 'rol');
  RolPrincipal.Checked := Texto = 'principal';
  RolUnica.Checked := Texto = 'unica';
  RolOtra.Checked := Texto = 'otra';
  Roles := Valor(Actual, 'roles');
  if Roles = '' then
    Roles := 'principal,unica,otra';
  if HayMarca then
  begin
    { Con la base ya sacada esta página no decide: viene «Otra caja» y lo demás apagado; terminar o cancelar se elige en la que sigue. }
    Roles := 'otra';
    RolOtra.Checked := True;
  end;
  RolPrincipal.Enabled := Pos('principal', Roles) > 0;
  RolUnica.Enabled := Pos('unica', Roles) > 0;
  RolOtra.Enabled := Pos('otra', Roles) > 0;
  Texto := Valor(Actual, 'roles-motivo');
  if HayMarca then
  begin
    Texto := 'La base de esta PC se sacó para mudarla';
    if Valor(Actual, 'mudanza-cuando') <> '' then
      Texto := Texto + ' (' + Valor(Actual, 'mudanza-cuando') + ')';
    Texto := Texto + '. En la página que sigue eliges terminar la mudanza o cancelarla.';
  end
  else if HayPos then
  begin
    if Texto <> '' then
      Texto := Texto + #13#10;
    Texto := Texto + 'Ya viene elegido lo que es hoy esta PC. Si la cambias de «Caja principal» a «La única caja» (o al revés), solo cambian '
      + 'su firewall y servicio.json.';
  end;
  NotaRol.Caption := Texto;
  PrepararLaMudanza;

  EditPrincipal.Text := Valor(Actual, 'principal');

  N := StrToIntDef(Valor(Actual, 'impresoras'), 0);
  SetArrayLength(ImpresorasNombres, N);
  SetArrayLength(ImpresorasAvisos, N);
  ComboTicket.Items.Add('En pantalla (sin imprimir)');
  ComboTicket.Items.Add('Por la red, con su IP (tcp://IP:9100)');
  ComboCola.Items.Add('Ninguna');
  for I := 1 to N do
  begin
    ImpresorasNombres[I - 1] := Valor(Actual, 'impresora.' + IntToStr(I));
    ImpresorasAvisos[I - 1] := Valor(Actual, 'impresora.' + IntToStr(I) + '.aviso');
    ComboTicket.Items.Add(ImpresorasNombres[I - 1]);
    ComboCola.Items.Add(ImpresorasNombres[I - 1]);
  end;
  Texto := Valor(Actual, 'ticket');
  if Texto = '' then
    ComboTicket.ItemIndex := 0
  else if EmpiezaCon(Texto, 'tcp://') then
  begin
    ComboTicket.ItemIndex := 1;
    Texto := Copy(Texto, 7, Length(Texto));
    if Copy(Texto, Length(Texto) - 4, 5) = ':9100' then
      Texto := Copy(Texto, 1, Length(Texto) - 5);
    EditTicketIp.Text := Texto;
  end
  else
  begin
    if ComboTicket.Items.IndexOf(Texto) < 2 then
      ComboTicket.Items.Add(Texto);
    ComboTicket.ItemIndex := ComboTicket.Items.IndexOf(Texto);
  end;
  Texto := Valor(Actual, 'cola');
  if Texto = '' then
    ComboCola.ItemIndex := 0
  else
  begin
    if ComboCola.Items.IndexOf(Texto) < 1 then
      ComboCola.Items.Add(Texto);
    ComboCola.ItemIndex := ComboCola.Items.IndexOf(Texto);
  end;

  Dividir(Valor(Actual, 'puertos'), ',', Puertos);
  for I := 0 to GetArrayLength(Puertos) - 1 do
    ComboPuerto.Items.Add(Puertos[I]);
  if GetArrayLength(Puertos) = 0 then
    NotaLector.Caption := 'Esta PC no tiene puertos COM ahora: si el lector es USB en modo COM, conéctalo antes.'
  else
    NotaLector.Caption := 'Los puertos COM de esta PC: ' + Valor(Actual, 'puertos') + '. Lo demás del puerto queda como lo usa casi siempre '
      + 'eleventa (8 bits, sin paridad, 1 de parada); si el suyo es distinto, el instructivo dice dónde se cambia.';
  Texto := Valor(Actual, 'lector-puerto');
  LectorCom.Checked := Texto <> '';
  LectorTeclado.Checked := Texto = '';
  ComboPuerto.Text := Texto;
  Texto := Valor(Actual, 'lector-baudios');
  if Texto = '' then
    Texto := '9600';
  ComboBaudios.Text := Texto;

  HabiaCodigo := TieneVariable('ControlPrint__Codigo');
  NotaCodigo.Caption := 'El código va solo en el servicio de esta PC (su variable), nunca en un archivo. La caja nunca le escribe a Control Print: '
    + 'lo que se cobre aquí sigue «por cobrar» allá.';
  if HabiaCodigo then
    NotaCodigo.Caption := 'El servicio ya tiene un código: déjalo vacío para conservarlo. ' + NotaCodigo.Caption;
  HabiaClave := TieneVariable('Eleventa__Clave');
  PrellenarElPuente;

  N := StrToIntDef(Valor(Actual, 'gbak'), 0);
  SetArrayLength(GbakRutas, N);
  SetArrayLength(GbakVersiones, N);
  SetArrayLength(GbakAvisos, N);
  for I := 1 to N do
  begin
    GbakRutas[I - 1] := Valor(Actual, 'gbak.' + IntToStr(I));
    GbakVersiones[I - 1] := Valor(Actual, 'gbak.' + IntToStr(I) + '.version');
    GbakAvisos[I - 1] := Valor(Actual, 'gbak.' + IntToStr(I) + '.aviso');
    ComboGbak.Items.Add(GbakRutas[I - 1]);
  end;
  ComboGbak.Text := Valor(Actual, 'eleventa-gbak');
  EditRespaldos.Text := Valor(Actual, 'eleventa-respaldos');
  NotaClave.Caption := 'Va solo en el servicio de esta PC (su variable), nunca en un archivo ni en un registro.';
  if HabiaClave then
    NotaClave.Caption := 'El servicio ya tiene la clave: déjala vacía para conservarla. ' + NotaClave.Caption;
end;

{ Lo que se dio de Control Print y de eleventa (/CONTROLPRINT=, /ELEVENTA=, su servidor, su base, su caja y su cajero). Aparte de
  AplicarParametros porque se repite después de llenar esas dos páginas con lo de un paquete de mudanza: lo que se pidió por parámetro
  manda sobre lo que propone el paquete. }
procedure AplicarParametrosDelPuente;
var
  Texto: String;
begin
  Texto := Parametro('CONTROLPRINT');
  if Texto <> SinValor then
  begin
    CpSi.Checked := Lowercase(Trim(Texto)) <> 'no';
    CpNo.Checked := not CpSi.Checked;
    if CpSi.Checked and (Lowercase(Trim(Texto)) <> 'si') then
      EditCpServidor.Text := Trim(Texto);
  end;

  Texto := Lowercase(Parametro('ELEVENTA'));
  if Texto = 'si' then
    ElSi.Checked := True
  else if Texto = 'no' then
    ElNo.Checked := True;
  ElNo.Checked := not ElSi.Checked;
  Texto := Parametro('ELEVENTASERVIDOR');
  if Texto <> SinValor then
    EditElServidor.Text := Trim(Texto);
  Texto := Parametro('ELEVENTABASE');
  if Texto <> SinValor then
    EditElBase.Text := Trim(Texto);
  Texto := Parametro('ELEVENTACAJA');
  if Texto <> SinValor then
    EditElCaja.Text := Trim(Texto);
  Texto := Parametro('ELEVENTACAJERO');
  if Texto <> SinValor then
    EditElCajero.Text := Trim(Texto);
end;

{ Sin preguntar: lo que se dé como /ROL=… manda sobre lo que ya tiene la PC (docs/instalacion.md, «Sin preguntar»). }
procedure AplicarParametros;
var
  Texto: String;
begin
  Texto := Lowercase(Parametro('ROL'));
  { Con la base ya sacada, «Qué es esta PC» no se cambia: decide la mudanza (un /ROL= que no cuadra con ella lo rechaza MudanzaQueNoAplica). }
  if HayMarca then
    Texto := '';
  if Texto = 'principal' then
    RolPrincipal.Checked := True
  else if Texto = 'otra' then
    RolOtra.Checked := True
  else if (Texto = 'unica') or (Texto = 'única') then
    RolUnica.Checked := True;

  Texto := Parametro('PRINCIPAL');
  if Texto <> SinValor then
    EditPrincipal.Text := Trim(Texto);

  Texto := Parametro('IMPRESORA');
  if Texto <> SinValor then
  begin
    Texto := Trim(Texto);
    if (Texto = '') or (Lowercase(Texto) = 'pantalla') then
      ComboTicket.ItemIndex := 0
    else if EmpiezaCon(Texto, 'tcp://') then
    begin
      ComboTicket.ItemIndex := 1;
      EditTicketIp.Text := Copy(Texto, 7, Length(Texto));
    end
    else
    begin
      if ComboTicket.Items.IndexOf(Texto) < 2 then
        ComboTicket.Items.Add(Texto);
      ComboTicket.ItemIndex := ComboTicket.Items.IndexOf(Texto);
    end;
  end;

  Texto := Parametro('COLA');
  if Texto <> SinValor then
  begin
    Texto := Trim(Texto);
    if (Texto = '') or (Lowercase(Texto) = 'ninguna') then
      ComboCola.ItemIndex := 0
    else
    begin
      if ComboCola.Items.IndexOf(Texto) < 1 then
        ComboCola.Items.Add(Texto);
      ComboCola.ItemIndex := ComboCola.Items.IndexOf(Texto);
    end;
  end;

  Texto := Parametro('LECTOR');
  if Texto <> SinValor then
  begin
    LectorCom.Checked := Lowercase(Trim(Texto)) <> 'teclado';
    LectorTeclado.Checked := not LectorCom.Checked;
    if LectorCom.Checked then
      ComboPuerto.Text := Trim(Texto);
  end;
  Texto := Parametro('BAUDIOS');
  if Texto <> SinValor then
    ComboBaudios.Text := Trim(Texto);

  { La mudanza de la base: la carpeta de su paquete y lo que se hace (/MUDANZA=sacar, traer, terminar o cancelar). Aquí solo se elige lo que
    esta PC ofrece; si lo pedido no quedó elegido (no aplica a esta PC), sin preguntar se rechaza antes de cambiar nada (MudanzaQueNoAplica). }
  Texto := Parametro('MUDANZAPAQUETE');
  if Texto <> SinValor then
    EditPaquete.Text := Trim(Texto);
  MudanzaEncima := Lowercase(Trim(Parametro('MUDANZAENCIMA'))) = 'si';
  Texto := MudanzaPedida;
  if HayMarca then
  begin
    if Texto = 'cancelar' then
      Mudanza2.Checked := True
    else if Texto = 'terminar' then
      Mudanza1.Checked := True;
  end
  else if Texto = 'traer' then
    Mudanza3.Checked := True
  else if (Texto = 'sacar') and TieneServicio then
    Mudanza2.Checked := True
  else if Texto = '' then
    Mudanza1.Checked := True;

  AplicarParametrosDelPuente;
  Texto := Parametro('ELEVENTAGBAK');
  if Texto <> SinValor then
    ComboGbak.Text := Trim(Texto);
  Texto := Parametro('ELEVENTARESPALDOS');
  if Texto <> SinValor then
    EditRespaldos.Text := Trim(Texto);

  { Sin preguntar, los secretos salen de las variables de entorno de este proceso: nunca de la línea de órdenes (quedaría en /LOG). }
  if WizardSilent then
  begin
    EditClave.Text := GetEnv('PAPELERIAPOS_CLAVE_ELEVENTA');
    EditCodigo.Text := GetEnv('PAPELERIAPOS_CODIGO_CONTROLPRINT');
  end;
end;

{ ── Al dar «Siguiente» ── }

{ Una caja principal (o la única caja) en una PC que todavía no tiene el POS: antes, si en la red ya contesta una caja principal (la misma
  búsqueda de la caja). Otra en la misma tienda tendría su propia base, aparte: lo seguro, el botón que viene elegido, es volver y elegir
  «Otra caja». Sin preguntar, solo se anota. }
function SinOtraPrincipal: Boolean;
var
  Salida: TArrayOfString;
  I, N: Integer;
  Lista, Antes, Volver, Seguir: String;
begin
  Result := True;
  Antes := NotaRol.Caption;
  NotaRol.Caption := 'Buscando en la red si ya hay una caja principal…';
  try
    if not CorrerAyudante('buscar', ExpandConstant('{tmp}\buscar-principal.txt'), Salida) then
    begin
      Log('No se pudo buscar en la red si ya hay una caja principal.');
      Exit;
    end;
    N := StrToIntDef(Valor(Salida, 'encontradas'), 0);
    if N = 0 then
    begin
      Log('En la red no contesta ninguna caja principal.');
      Exit;
    end;
    Lista := '';
    for I := 1 to N do
      Lista := Lista + '• ' + Valor(Salida, 'encontrada.' + IntToStr(I) + '.texto') + #13#10;
    Log('En la red ya contesta una caja principal: ' + Lista);
    if WizardSilent then
      Exit;
    { TaskDialogMsgBox le da IDYES al primer botón, que es el que viene elegido (Enter): el seguro, volver. (Un renglón del Pascal no
      puede empezar con «[»: Inno lo tomaría por una sección.) }
    Volver := 'No, volver' + #13#10 + 'Esta PC es de esa tienda: elige «Otra caja», que se conecta a esa caja principal.';
    Seguir := 'Sí, es otra tienda' + #13#10 + 'Esta PC lleva su propia base, aparte de la de esa caja principal.';
    Result := TaskDialogMsgBox('Ya hay una caja principal en la red',
      'En esta red ya contesta una caja principal de Papelería POS:' + #13#10 + Lista + #13#10
      + 'Otra caja principal tendría su propia base, aparte: lo que se venda en una no se ve en la otra.', mbError, MB_YESNO, [Volver, Seguir], 0) = IDNO;
    if not Result then
      Log('Se volvió a la página «Qué es esta PC».');
  finally
    NotaRol.Caption := Antes;
  end;
end;

{ Lo marcado en «Qué es esta PC» (con la base ya sacada, siempre «Otra caja»: lo que cuenta lo decide la página siguiente). }
function RevisarRol: Boolean;
begin
  Result := True;
  if RolMarcado = '' then
    Result := Rechazar('Elige qué es esta PC (sin preguntar: /ROL=principal, /ROL=otra o /ROL=unica).')
  else if Pos(RolMarcado, Roles) = 0 then
    Result := Rechazar('Esta PC no puede ser «' + NombreDelRol(RolMarcado) + '»: ' + Valor(Actual, 'roles-motivo'))
  else if ConServicio and not HayPos then
    Result := SinOtraPrincipal;
end;

{ Al aceptar «La base de la tienda»: si se trae una base, las páginas de Control Print y de eleventa empiezan con lo de su paquete, una vez por
  paquete (lo que después se cambie en esas páginas se respeta); si ya no se trae, vuelven a lo de esta PC. Lo que se dio por parámetro manda
  sobre lo que propone el paquete. }
procedure AcomodarElPuente;
begin
  if MudanzaElegida = 'traer' then
  begin
    if PaqueteAplicado = IdDelPaquete then
      Exit;
    PonerLoDelPaquete;
  end
  else
  begin
    if PaqueteAplicado = '' then
      Exit;
    PrellenarElPuente;
  end;
  AplicarParametrosDelPuente;
  ActualizarControlPrint;
  ActualizarEleventa;
end;

{ El traida.txt que deja en el paquete la PC a donde se trajo la base de esta (con sus IP), si es de este mismo paquete: el de la marca de esta
  PC, que dijo el ayudante. Se busca en la carpeta escrita (la «mudanza» junto al instalador, o la de /MUDANZAPAQUETE=), en la «mudanza» junto
  al instalador y en la carpeta a donde se sacó. Dice en cuál se vio y la dirección de esa PC. No cambia nada: solo lee. }
function LaTraida(var Carpeta, Ips, Puerto: String): Boolean;
var
  Carpetas, Lineas: TArrayOfString;
  I: Integer;
begin
  Result := False;
  Carpeta := '';
  Ips := '';
  Puerto := '';
  if Trim(Valor(Actual, 'mudanza-paquete')) = '' then
    Exit;
  SetArrayLength(Carpetas, 3);
  Carpetas[0] := CarpetaDelPaquete;
  Carpetas[1] := RemoveBackslashUnlessRoot(ExpandConstant('{src}\mudanza'));
  Carpetas[2] := RemoveBackslashUnlessRoot(Trim(Valor(Actual, 'mudanza-carpeta')));
  for I := 0 to 2 do
  begin
    if Carpetas[I] = '' then
      Continue;
    if not LeerClaves(AddBackslash(Carpetas[I]) + 'traida.txt', Lineas) then
      Continue;
    if CompareText(Trim(Valor(Lineas, 'paquete')), Trim(Valor(Actual, 'mudanza-paquete'))) = 0 then
    begin
      Carpeta := Carpetas[I];
      Ips := Valor(Lineas, 'ips');
      Puerto := Trim(Valor(Lineas, 'puerto'));
      Result := True;
      Exit;
    end;
  end;
end;

{ Al cancelar con el aviso traida.txt de este paquete a la vista: la USB prueba que la base de esta PC ya se trajo a la otra, y allá puede
  seguir viva aunque ahora no conteste (apagada, o la red no deja verla). Cancelar así dejaría dos PC con la misma base, cada una con sus
  ventas. Solo se sigue si se confirma que allá ya se desinstaló borrando los datos; lo seguro, no, es lo que viene elegido. Con «Sí» va en
  las respuestas (mudanza-aunque-traida): instalar-pc.ps1 vuelve a mirar ese aviso y sin esa respuesta no cancela. Sin preguntar no hay quien
  lo confirme: no se cancela. Sin el aviso a la vista no hay nada que preguntar: instalar-pc.ps1 lo revisa en la red, como siempre. }
function ConfirmarCancelar: Boolean;
var
  Carpeta, Ips, Puerto, Donde, Texto: String;
  Lineas: TArrayOfString;
begin
  Result := True;
  if not LaTraida(Carpeta, Ips, Puerto) then
    Exit;
  { A dónde se trajo, como lo dice ese aviso (el nombre de esa PC y sus IP), sin que un archivo raro alargue la pregunta. }
  Donde := '';
  if LeerClaves(AddBackslash(Carpeta) + 'traida.txt', Lineas) then
    Donde := Copy(Trim(Valor(Lineas, 'equipo')), 1, 63);
  Ips := Copy(Trim(Ips), 1, 120);
  StringChangeEx(Ips, ';', ', ', True);
  if (Donde <> '') and (Ips <> '') then
    Donde := Donde + ', ' + Ips
  else if Ips <> '' then
    Donde := Ips;
  if Donde <> '' then
    Donde := ' (' + Donde + ')';
  Texto := 'La USB de la mudanza dice que la base de esta PC ya se trajo a la otra PC' + Donde;
  if WizardSilent then
  begin
    Result := Rechazar(Texto + ': sin preguntar no se cancela, porque si allá sigue instalada quedarían dos PC con la misma base. Si allá ya se '
      + 'desinstaló con «Borrar también los datos», cancela con el asistente a la vista, que lo pregunta.');
    Exit;
  end;
  Texto := Texto + '. Si allá sigue instalada, al cancelar quedan dos PC con la misma base.' + #13#10#13#10
    + '¿Allá ya se desinstaló Papelería POS con «Borrar también los datos»?' + #13#10#13#10
    + 'Si no, o si no estás seguro, contesta «No» (no se cambia nada): primero se desinstala allá con esa casilla; o, si allá la mudanza quedó '
    + 'bien, aquí se elige «Terminar la mudanza».';
  Log('Pregunta: ' + Texto);
  if MsgBox(Texto, mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
  begin
    Log('Se contestó que sí: se pide cancelar aunque la USB diga que la base ya se trajo.');
    CancelarAunqueTraida := True;
  end
  else
  begin
    Log('No se confirmó: no se cancela.');
    Result := False;
  end;
end;

{ Si esa carpeta está en el disco de Windows de esta PC o en su carpeta temporal (el instalador abierto desde dentro de un .zip, o desde una
  copia en el escritorio), y no en una USB. }
function EnElDiscoDeEstaPc(const Carpeta: String): Boolean;
begin
  Result := EmpiezaCon(Carpeta, AddBackslash(ExpandConstant('{sd}'))) or EmpiezaCon(AddBackslash(Carpeta), GetTempDir);
end;

{ «La base de la tienda»: para sacar o traer la base hace falta la carpeta del paquete, y para traerla, que el ayudante diga que ese paquete
  se puede traer (mudanza-ver). Lo demás lo decide, antes de cambiar nada, la revisión completa (al instalar). }
function RevisarMudanza: Boolean;
var
  Elegida, Texto: String;
begin
  Result := True;
  { A medio revisar un paquete (se oprimió dos veces): todavía no. }
  if ViendoElPaquete then
  begin
    Result := False;
    Exit;
  end;
  { Traer la base encima de la tienda que ya tiene esta PC se acepta cada vez (o con /MUDANZAENCIMA=si): no se arrastra de un paquete a otro.
    Y cancelar con el aviso de que la base ya se trajo a la otra PC se confirma cada vez. }
  MudanzaEncima := Lowercase(Trim(Parametro('MUDANZAENCIMA'))) = 'si';
  CancelarAunqueTraida := False;
  Elegida := MudanzaElegida;
  { Cancelar deja la base en esta PC, con su servicio. Si el ayudante ya no la ve como «Caja principal» ni «La única caja» (una mudanza que
    se empezó a terminar y se quedó a medias: ya no tiene el servicio), desde aquí solo queda terminarla. }
  if (Elegida = 'cancelar') and not ConServicio then
    Result := Rechazar('Esta PC ya no tiene el servicio con la base de la tienda (la mudanza se empezó a terminar): ya no se cancela desde aquí. '
      + 'Elige «Terminar la mudanza».')
  else if Elegida = 'cancelar' then
    Result := ConfirmarCancelar
  else if (Elegida = 'sacar') or (Elegida = 'traer') then
  begin
    if CarpetaDelPaquete = '' then
      Result := Rechazar('Escribe la carpeta del paquete de mudanza, o elígela con «Buscar…» (sin preguntar: /MUDANZAPAQUETE=).')
    else if not EsRutaCompleta(CarpetaDelPaquete) then
      Result := Rechazar('La carpeta del paquete de mudanza va con su ruta completa (por ejemplo, E:\mudanza).')
    else if Elegida = 'traer' then
    begin
      VerElPaquete;
      ActualizarMudanza;
      if Valor(Paquete, 'paquete') <> 'bien' then
        Result := Rechazar('El paquete de mudanza de ' + CarpetaDelPaquete + ' no se puede traer: ' + TextoDelPaquete);
    end
    else if not WizardSilent and EnElDiscoDeEstaPc(CarpetaDelPaquete) then
    begin
      { Al sacar la base esta PC deja de atender: si su paquete no queda en la USB, no llega a la otra PC y habría que cancelar y repetir, con
        la tienda detenida. Pasa si el instalador se abrió desde dentro del .zip o desde una copia en esta PC. Lo seguro, no, viene elegido. }
      Texto := 'El paquete de mudanza quedaría en ' + CarpetaDelPaquete + '.' + #13#10#13#10
        + 'Esa carpeta está en esta PC, no en la USB: ¿dejar ahí el paquete?' + #13#10#13#10
        + 'Con «No» no se cambia nada: elige con «Buscar…» la carpeta «mudanza» de la USB, o abre este instalador desde la USB. Con «Sí», después '
        + 'hay que copiar esa carpeta a la USB para llevarla a la otra PC.';
      Log('Pregunta: ' + Texto);
      Result := MsgBox(Texto, mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES;
    end;
  end;
  if Result then
    AcomodarElPuente;
end;

{ La primera vez que se llega a «La base de la tienda» con el asistente a la vista, qué viene elegido: «Traer la base de otra PC» si en la
  carpeta de siempre (la «mudanza» junto al instalador) hay un paquete que se puede traer y esta PC todavía no tiene el servicio (es la PC
  nueva), o ya se le puso la base de ese mismo paquete (es un segundo intento, para terminarlo). En cualquier otro caso, sin mudanza. No le
  pregunta nada al servicio. Con la base ya sacada no hay paquete que ver; lo que se pidió con /MUDANZA= ya quedó elegido; y sin preguntar no
  se propone nada: ahí manda /MUDANZA=. Sin esa carpeta ni se corre el ayudante (lo de todos los días). }
procedure AlLlegarALaMudanza;
var
  Antes: Boolean;
begin
  if MudanzaVista or WizardSilent then
    Exit;
  MudanzaVista := True;
  if HayMarca or (MudanzaPedida <> SinValor) or not DirExists(CarpetaDelPaquete) then
    Exit;
  { Mientras el ayudante ve el paquete, otro «Siguiente» no cuenta. }
  Antes := Ocupado;
  Ocupado := True;
  try
    VerElPaquete;
    if (Valor(Paquete, 'paquete') = 'bien') and (not TieneServicio or PaqueteYaPuesto) then
    begin
      Mudanza3.Checked := True;
      { Y con el foco: la página ya está a la vista, con el foco en la opción que se acaba de desmarcar, y en Windows un botón de opción sin
        marcar se marca solo en cuanto vuelve a recibir el foco (al regresar de otra ventana, por ejemplo). Sin esto, «Traer» se desmarcaría. }
      if Mudanza3.CanFocus then
        WizardForm.ActiveControl := Mudanza3;
    end;
    ActualizarMudanza;
  finally
    Ocupado := Antes;
  end;
end;

{ Sin preguntar: por qué en esta PC no se puede hacer la mudanza que se pidió con /MUDANZA= (o por qué hace falta pedirla); vacío si se
  puede. No se ignora: PrepareToInstall termina con eso antes de cambiar nada, también cuando «La base de la tienda» ni siquiera salió (con
  «Otra caja» se salta, y entonces nadie más lo revisaría). }
function MudanzaQueNoAplica: String;
var
  Pedida, Rol: String;
begin
  Result := '';
  Pedida := MudanzaPedida;
  if Pedida = SinValor then
    Pedida := '';
  Rol := Lowercase(Trim(Parametro('ROL')));
  if Rol = 'única' then
    Rol := 'unica';
  if (Pedida <> '') and (Pedida <> 'sacar') and (Pedida <> 'traer') and (Pedida <> 'terminar') and (Pedida <> 'cancelar') then
    Result := 'La mudanza va como /MUDANZA=sacar, traer, terminar o cancelar (está «' + Pedida + '»).'
  else if HayMarca then
  begin
    if (Pedida <> 'terminar') and (Pedida <> 'cancelar') then
      Result := 'La base de esta PC se sacó para mudarla a otra PC: sin preguntar hay que decir qué sigue, /MUDANZA=terminar (con /PRINCIPAL=) o '
        + '/MUDANZA=cancelar.'
    else if (Rol <> SinValor) and (Rol <> RolElegido) then
      Result := 'Con la base de esta PC ya sacada, /ROL= no la cambia: con /MUDANZA=terminar queda como «Otra caja» (/ROL=otra, o sin /ROL=) y con '
        + '/MUDANZA=cancelar, como lo que es hoy (sin /ROL=).';
  end
  else if (Pedida = 'terminar') or (Pedida = 'cancelar') then
    Result := 'En esta PC no hay una mudanza a medias (su base no se ha sacado): /MUDANZA=' + Pedida + ' no aplica.'
  else if (Pedida <> '') and not ConServicio then
    Result := 'La base de la tienda solo se saca o se trae en una PC con servicio («Caja principal» o «La única caja»): /MUDANZA=' + Pedida
      + ' no va con «Otra caja».'
  else if (Pedida = 'sacar') and not TieneServicio then
    Result := 'Esta PC no tiene el servicio con la base de la tienda: no hay nada que mudar (/MUDANZA=sacar no aplica).'
  else if Pedida <> MudanzaElegida then
    Result := 'No se pudo elegir /MUDANZA=' + Pedida + ' en esta PC.';
end;

{ Al terminar una mudanza, la dirección de la caja principal empieza con la que trae la USB (el traida.txt del paquete): de sus IP, la primera
  que contesta (se prueban hasta cuatro); si ninguna, la primera. Una vez: si después se borra o se cambia, se respeta. Mientras prueba, otro
  «Siguiente» no cuenta. Lo que trae ese archivo solo se usa si tiene la forma de una dirección. }
procedure ProponerLaPrincipal;
var
  Carpeta, Ips, Puerto, Direccion, Primera, Texto, Aviso: String;
  Lista: TArrayOfString;
  I, Probadas: Integer;
  Antes: Boolean;
begin
  if PrincipalPropuesta or (MudanzaElegida <> 'terminar') or (Trim(EditPrincipal.Text) <> '') then
    Exit;
  if not LaTraida(Carpeta, Ips, Puerto) then
    Exit;
  PrincipalPropuesta := True;
  Primera := '';
  Probadas := 0;
  Dividir(Ips, ';', Lista);
  Antes := Ocupado;
  Ocupado := True;
  BotonBuscar.Enabled := False;
  BotonProbar.Enabled := False;
  EstadoPrincipal.Caption := 'Probando la dirección que trae la USB de la mudanza…';
  try
    for I := 0 to GetArrayLength(Lista) - 1 do
    begin
      Direccion := Trim(Lista[I]);
      if Direccion = '' then
        Continue;
      if (Puerto <> '') and (Puerto <> '5080') then
        Direccion := Direccion + ':' + Puerto;
      if not EsDireccion(Direccion) then
        Continue;
      if Primera = '' then
        Primera := Direccion;
      if Probadas >= 4 then
        Break;
      Probadas := Probadas + 1;
      if ProbarPrincipal(Direccion, Texto, Aviso) then
      begin
        EditPrincipal.Text := Direccion;
        EstadoPrincipal.Caption := 'Es la dirección que trae la USB de la mudanza. Contesta: ' + Texto;
        if Aviso <> '' then
          EstadoPrincipal.Caption := EstadoPrincipal.Caption + #13#10 + 'Ojo: ' + Aviso;
        Exit;
      end;
    end;
    EditPrincipal.Text := Primera;
    if Primera = '' then
      EstadoPrincipal.Caption := ''
    else
      EstadoPrincipal.Caption := 'Es la dirección que trae la USB de la mudanza, pero ahora no contestó: revisa que esa PC esté prendida y que '
        + 'allá ya haya terminado este instalador, o búscala en la red.';
  finally
    BotonBuscar.Enabled := True;
    BotonProbar.Enabled := True;
    Ocupado := Antes;
  end;
end;

{ Al llegar a «Dónde está la caja principal». Al terminar una mudanza esa dirección es la de la PC que ya tiene la base de esta: hace falta, y
  viene la que trae la USB. En los demás casos, lo de siempre. Sin preguntar no se propone nada: va con /PRINCIPAL=. }
procedure AlLlegarALaPrincipal;
begin
  if MudanzaElegida = 'terminar' then
    EtiquetaPrincipal.Caption := 'La IP de la PC que ya tiene la base de esta (viene la que trae la USB de la mudanza; si no, búscala en la red). '
      + 'Hace falta para terminar la mudanza.'
  else
    EtiquetaPrincipal.Caption := DondeEstaLaPrincipal;
  if not WizardSilent then
    ProponerLaPrincipal;
end;

function RevisarPrincipal: Boolean;
var
  Texto, Respuesta, Aviso: String;
begin
  Result := True;
  Texto := Trim(EditPrincipal.Text);
  if Texto = '' then
  begin
    { Para terminar una mudanza hace falta: instalar-pc.ps1 le pregunta a esa PC si ya tiene la base de esta antes de cambiar nada aquí. }
    if MudanzaElegida = 'terminar' then
      Result := Rechazar('Para terminar la mudanza hace falta la dirección de la caja principal, la PC a donde se llevó la base: escribe su IP o '
        + 'búscala en la red (sin preguntar: /PRINCIPAL=).');
    Exit;
  end;
  if not EsDireccion(Texto) then
  begin
    Respuesta := 'La caja principal va con su IP (por ejemplo, 192.168.1.10; con su puerto, de 1 a 65535, si no es el 5080)';
    if MudanzaElegida = 'terminar' then
      Result := Rechazar(Respuesta + '.')
    else
      Result := Rechazar(Respuesta + ', o vacía para que la caja la busque al abrirla.');
  end
  else if ProbarPrincipal(Texto, Respuesta, Aviso) then
  begin
    EstadoPrincipal.Caption := 'Contesta: ' + Respuesta;
    if Aviso <> '' then
    begin
      EstadoPrincipal.Caption := EstadoPrincipal.Caption + #13#10 + 'Ojo: ' + Aviso;
      if MudanzaElegida = 'terminar' then
        Result := Preguntar('La caja principal en ' + Texto + ' contesta, pero: ' + Aviso + #13#10#13#10
          + '¿Seguir de todos modos? Para terminar la mudanza esa PC ya debe tener la base de esta: si no, al instalar no se cambia nada.', IDYES)
      else
        Result := Preguntar('La caja principal en ' + Texto + ' contesta, pero: ' + Aviso + #13#10#13#10
          + '¿Instalar esta caja de todos modos? Se da de alta cuando eso se arregle.', IDYES);
    end;
  end
  else
  begin
    EstadoPrincipal.Caption := 'No contestó: ' + Respuesta;
    if Copy(Respuesta, Length(Respuesta), 1) = '.' then
      Respuesta := Copy(Respuesta, 1, Length(Respuesta) - 1);
    if MudanzaElegida = 'terminar' then
      Result := Preguntar('La caja principal en ' + Texto + ' no contestó (' + Respuesta + ').' + #13#10#13#10
        + '¿Seguir de todos modos? Para terminar la mudanza tiene que contestar (allá ya debe haber terminado este instalador): si al instalar '
        + 'sigue sin contestar, no se cambia nada.', IDYES)
    else
      Result := Preguntar('La caja principal en ' + Texto + ' no contestó (' + Respuesta + ').' + #13#10#13#10
        + '¿Seguir de todos modos? La caja la busca ahí al abrirla; si no está, la busca en la red.', IDYES);
  end;
end;

function RevisarImpresoras: Boolean;
begin
  Result := True;
  if (ComboTicket.ItemIndex = 1) and not EsDireccion(Trim(EditTicketIp.Text)) then
    Result := Rechazar('Escribe la IP de la impresora de tickets (por ejemplo, 192.168.1.100; con su puerto, de 1 a 65535, si no es el 9100).')
  else if AvisoCola.Caption <> '' then
    Result := Preguntar('Ojo: ' + AvisoCola.Caption + #13#10#13#10 + '¿Seguir con esa?', IDYES);
end;

function RevisarLector: Boolean;
var
  Puerto: String;
begin
  Result := True;
  if not LectorCom.Checked then
    Exit;
  Puerto := Uppercase(Trim(ComboPuerto.Text));
  if EmpiezaCon(Puerto, 'COM') then
    Puerto := Copy(Puerto, 4, Length(Puerto));
  { Como el ayudante (Respuestas.NombreDelPuerto): de COM1 a COM255. }
  if not SoloCifras(Puerto) or (Length(Puerto) > 3) or (StrToIntDef(Puerto, 0) < 1) or (StrToIntDef(Puerto, 0) > 255) then
    Result := Rechazar('Elige el puerto COM del lector (de COM1 a COM255).')
  else if not SoloCifras(Trim(ComboBaudios.Text)) or (Length(Trim(ComboBaudios.Text)) > 6) or (StrToIntDef(Trim(ComboBaudios.Text), 0) < 50)
    or (StrToIntDef(Trim(ComboBaudios.Text), 0) > 921600) then
    Result := Rechazar('La velocidad del lector va en baudios, de 50 a 921600, como la tiene eleventa (por ejemplo, 9600).');
end;

function RevisarControlPrint: Boolean;
begin
  Result := True;
  if not CpSi.Checked then
    Exit;
  if not EsDireccion(Trim(EditCpServidor.Text)) then
    Result := Rechazar('Escribe la IP de la PC de Control Print (por ejemplo, 192.168.1.106).')
  else if (EditCodigo.Text <> '') and (Length(CodigoNormal(EditCodigo.Text)) <> 16) then
    Result := Rechazar('El código de conexión tiene 16 letras y números (como ABCD-EFGH-JKLM-NPQR): revísalo en Control Print → Ajustes → Red.')
  else if (EditCodigo.Text = '') and not HabiaCodigo then
    Result := Preguntar('Sin el código de conexión, Control Print no le contesta a la caja. ¿Seguir sin él? (Se pone después, corriendo el '
      + 'instalador otra vez.)', IDYES);
end;

function RevisarEleventa: Boolean;
begin
  Result := True;
  if not ElSi.Checked then
    Exit;
  if not EsDireccion(Trim(EditElServidor.Text)) then
    Result := Rechazar('Escribe la IP de la PC de eleventa (la que dice IPServidor en su pdventa.ini).')
  else if Trim(EditElBase.Text) = '' then
    Result := Rechazar('Escribe la ruta de la base de eleventa en esa PC (por ejemplo, ' + 'C:\Program Files (x86)\AbarrotesMultiCaja\db\PDVDATA.FDB).')
  else if not NumeroDeEleventa(Trim(EditElCaja.Text)) or not NumeroDeEleventa(Trim(EditElCajero.Text)) then
    Result := Rechazar('El número de la caja y el del cajero en eleventa van con cifras (o vacíos, o 0, si todavía no se saben).');
end;

function RevisarEleventa2: Boolean;
var
  VersionGbak: String;
begin
  Result := True;
  if not ElSi.Checked then
    Exit;
  if Pos('onedrive', Lowercase(EditRespaldos.Text)) > 0 then
    Result := Rechazar('Los respaldos de eleventa nunca van en OneDrive (son datos de la tienda): elige una carpeta de esta PC.')
  else if (Trim(ComboGbak.Text) <> '') and not FileExists(Trim(ComboGbak.Text)) then
    Result := Preguntar('No está ese gbak.exe en esta PC. ¿Seguir así? (Sin él no hay respaldo propio de eleventa.)', IDYES)
  else if (Trim(ComboGbak.Text) <> '') and GetVersionNumbersString(Trim(ComboGbak.Text), VersionGbak) and (Copy(VersionGbak, 1, 4) <> '2.0.') then
    Result := Preguntar('Ese gbak.exe es de Firebird ' + VersionGbak + ' y hace falta el de 2.0, como el de la tienda: con otro, el respaldo de eleventa podría no servir al '
      + 'restaurarlo. ¿Seguir así?', IDNO)
  else if (EditClave.Text = '') and not HabiaClave then
    Result := Preguntar('Sin la clave de Firebird, la caja no lee eleventa. ¿Seguir sin ella? (Se pone después, corriendo el instalador otra vez.)', IDYES);
end;

{ ── Instalar ── }

{ Si WebView2 está en ese lugar del registro, como lo dice Microsoft: su «pv» con una versión que no es 0.0.0.0. }
function WebView2En(const Raiz: Integer): Boolean;
var
  Version: String;
begin
  Result := False;
  Version := '';
  if RegQueryStringValue(Raiz, ClaveWebView2, 'pv', Version) then
    Result := (Version <> '') and (Version <> '0.0.0.0');
end;

function HayWebView2: Boolean;
begin
  Result := WebView2En(HKLM32) or WebView2En(HKLM64) or WebView2En(HKCU);
end;

procedure Estado(const Texto: String);
begin
  Log(Texto);
  WizardForm.StatusLabel.Caption := Texto;
  WizardForm.FilenameLabel.Caption := '';
end;

{ WebView2 (lo necesita la caja para su pantalla): si no está, el instalador de Microsoft que viene adentro, que lo baja de internet. }
procedure RevisarWebView2;
var
  Codigo: Integer;
begin
  if HayWebView2 then
  begin
    WebView2Dice := 'ya-estaba';
    Exit;
  end;
  Estado('Instalando WebView2 de Microsoft (lo necesita la caja; lo baja de internet)…');
  ExtractTemporaryFile('MicrosoftEdgeWebview2Setup.exe');
  if Exec(ExpandConstant('{tmp}\MicrosoftEdgeWebview2Setup.exe'), '/silent /install', ExpandConstant('{tmp}'), SW_HIDE, ewWaitUntilTerminated, Codigo)
    and HayWebView2 then
    WebView2Dice := 'instalado'
  else
  begin
    WebView2Dice := 'fallo';
    Log('WebView2 no se instaló (código ' + IntToStr(Codigo) + ').');
  end;
end;

function RespuestasArchivo: String;
begin
  Result := ExpandConstant('{tmp}\respuestas.txt');
end;

{ Las respuestas para instalar-pc.ps1 y el ayudante: sin las claves (de esas solo si hay una nueva). }
procedure GuardarRespuestas;
var
  Lineas: TArrayOfString;
  Carpeta, Ips, Puerto: String;
begin
  Agregar(Lineas, 'rol', RolElegido);
  if RolElegido = 'otra' then
    Agregar(Lineas, 'principal', Trim(EditPrincipal.Text));
  Agregar(Lineas, 'impresora', ValorTicket);
  Agregar(Lineas, 'cola', ValorCola);
  if LectorCom.Checked then
  begin
    Agregar(Lineas, 'lector', 'com');
    Agregar(Lineas, 'lector-puerto', Trim(ComboPuerto.Text));
    Agregar(Lineas, 'lector-baudios', Trim(ComboBaudios.Text));
  end
  else
    Agregar(Lineas, 'lector', 'teclado');
  if ConServicio then
  begin
    if CpSi.Checked then
    begin
      Agregar(Lineas, 'controlprint', 'si');
      Agregar(Lineas, 'controlprint-servidor', Trim(EditCpServidor.Text));
    end
    else
      Agregar(Lineas, 'controlprint', 'no');
    if ElSi.Checked then
    begin
      Agregar(Lineas, 'eleventa', 'si');
      Agregar(Lineas, 'eleventa-servidor', Trim(EditElServidor.Text));
      Agregar(Lineas, 'eleventa-base', Trim(EditElBase.Text));
      Agregar(Lineas, 'eleventa-caja', Trim(EditElCaja.Text));
      Agregar(Lineas, 'eleventa-cajero', Trim(EditElCajero.Text));
      Agregar(Lineas, 'eleventa-gbak', Trim(ComboGbak.Text));
      Agregar(Lineas, 'eleventa-respaldos', Trim(EditRespaldos.Text));
    end
    else
      Agregar(Lineas, 'eleventa', 'no');
  end;
  { La mudanza de la base, solo si se eligió una (sin ella, las respuestas son las de siempre). La carpeta del paquete: para sacar, dónde
    queda; para traer, dónde está; para terminar o cancelar, dónde se vio el traida.txt de este paquete (si se vio): es lo que prueba que la
    base ya se trajo a esa PC, y trae su dirección. Al cancelar, si no se vio, va la carpeta del paquete a secas (la «mudanza» junto al
    instalador): ahí lo busca también instalar-pc.ps1, que lo borra si es el de esta mudanza (la USB pudo cambiar de letra desde que se sacó).
    Traerla encima de la tienda que ya tiene esta PC, o cancelar aunque ese aviso diga que la base ya se trajo, solo si se aceptó. }
  if MudanzaElegida <> '' then
  begin
    Agregar(Lineas, 'mudanza', MudanzaElegida);
    if (MudanzaElegida = 'sacar') or (MudanzaElegida = 'traer') then
      Agregar(Lineas, 'mudanza-paquete', CarpetaDelPaquete)
    else if LaTraida(Carpeta, Ips, Puerto) then
      Agregar(Lineas, 'mudanza-paquete', Carpeta)
    else if (MudanzaElegida = 'cancelar') and EsRutaCompleta(CarpetaDelPaquete) then
      Agregar(Lineas, 'mudanza-paquete', CarpetaDelPaquete);
    if (MudanzaElegida = 'traer') and MudanzaEncima then
      Agregar(Lineas, 'mudanza-encima', 'si');
    if (MudanzaElegida = 'cancelar') and CancelarAunqueTraida then
      Agregar(Lineas, 'mudanza-aunque-traida', 'si');
  end;
  if Lowercase(Parametro('CERRARCAJA')) = 'si' then
    Agregar(Lineas, 'cerrar-caja', 'si')
  else
    Agregar(Lineas, 'cerrar-caja', 'no');
  SaveStringsToUTF8File(RespuestasArchivo, Lineas, False);
end;

{ La tienda que ya tiene esta PC, como la dice el ayudante en su error de «Esta PC ya tiene una tienda (<la tienda>): la base de…»; vacía si
  no viene dicha así. }
function TiendaDeEstaPc(const Dicho: String): String;
var
  Abre, Cierra: Integer;
  Resto: String;
begin
  Result := '';
  Abre := Pos('ya tiene una tienda', Lowercase(Dicho));
  if Abre = 0 then
    Exit;
  Resto := Copy(Dicho, Abre + Length('ya tiene una tienda'), Length(Dicho));
  if Copy(Resto, 1, 2) <> ' (' then
    Exit;
  Resto := Copy(Resto, 3, Length(Resto));
  Cierra := Pos('): la base de ', Resto);
  if Cierra > 1 then
    Result := Trim(Copy(Resto, 1, Cierra - 1));
end;

{ Antes de traer una base, la revisión completa (la orden «mudanza-revisar» del ayudante, que no cambia nada): el paquete entero, que la PC de
  donde salió no haya vuelto a trabajar, la base que tiene esta PC y cómo quedaría el puente con las respuestas. Falso si no se puede (Texto
  dice por qué; Clave, la página donde se corrige). La única pregunta: si esta PC ya tiene una tienda, si se aparta para traer la otra (lo
  seguro, no, es lo que viene elegido; sin preguntar es un error, salvo con /MUDANZAENCIMA=si). }
function RevisarElPaquete(var Clave, Texto: String): Boolean;
var
  Salida: TArrayOfString;
  Vuelta: Integer;
  Equipo, Tienda, Pregunta: String;
begin
  Result := False;
  for Vuelta := 1 to 2 do
  begin
    Clave := 'mudanza-paquete';
    if not CorrerAyudante('mudanza-revisar --paquete ' + EntreComillas(CarpetaDelPaquete) + ' --respuestas "' + RespuestasArchivo + '" --version "'
      + VersionDelInstalador + '"', ExpandConstant('{tmp}\mudanza-revisar.txt'), Salida) then
    begin
      Texto := 'No se pudo revisar el paquete de mudanza (el ayudante del instalador no corrió): no se cambió nada. Revisa que el antivirus no '
        + 'lo esté deteniendo y vuelve a correr el instalador.';
      Exit;
    end;
    Texto := Valor(Salida, 'error');
    if (Texto = '') and (Valor(Salida, 'bien') = 'si') then
    begin
      Log('El paquete de mudanza se puede traer (ya estaba puesto: ' + Valor(Salida, 'ya-estaba') + ').');
      Result := True;
      Exit;
    end;
    if Texto = '' then
      Texto := 'El ayudante del instalador no dijo si el paquete de mudanza se puede traer.'
    else
      Clave := Valor(Salida, 'clave');
    Log('La revisión de la mudanza no pasa (' + Clave + '): ' + Texto);
    { El único error que se puede aceptar aquí es el de «Esta PC ya tiene una tienda» (así lo dice el ayudante, con la clave «mudanza»). }
    if (Vuelta = 2) or WizardSilent or MudanzaEncima or (Clave <> 'mudanza') or (Pos('ya tiene una tienda', Lowercase(Texto)) = 0) then
      Exit;
    Equipo := Valor(Salida, 'equipo');
    if Equipo = '' then
      Equipo := EquipoDelPaquete;
    Pregunta := 'Esta PC ya tiene una tienda. ¿Apartarla (no se borra) y traer la de ' + Equipo + '?';
    { Cuál tiene (la dice el ayudante), para saber qué se aparta. Si se llama como la del paquete, lo más probable es que sea esa misma base,
      de un intento anterior de traerla que se cortó antes de dejar su nota; pero el nombre no lo prueba, y por eso se dice qué pasa con «Sí». }
    Tienda := TiendaDeEstaPc(Texto);
    if Tienda <> '' then
    begin
      Pregunta := Pregunta + #13#10#13#10 + 'La tienda de esta PC: ' + Tienda + '.';
      if CompareText(Tienda, Trim(Valor(Paquete, 'tienda'))) = 0 then
        Pregunta := Pregunta + ' Es la misma tienda del paquete: parece de un intento anterior de traerla. Con «Sí» se pone otra vez la del '
          + 'paquete, y lo que se haya cobrado en esta PC desde entonces se queda en la base apartada.';
    end;
    Log('Pregunta: ' + Pregunta);
    if MsgBox(Pregunta, mbConfirmation, MB_YESNO or MB_DEFBUTTON2) <> IDYES then
      Exit;
    Log('Se aceptó apartar la tienda de esta PC.');
    MudanzaEncima := True;
    GuardarRespuestas;
  end;
end;

{ Antes de cambiar nada en la PC, el ayudante revisa las respuestas como las va a usar (la orden «revisar», que no escribe nada) y, si se trae
  una base, su paquete. Falso si algo no se puede usar (Texto dice qué y por qué; Clave, su página) o si el ayudante no corrió. }
function RevisarConElAyudante(var Clave, Texto: String): Boolean;
var
  Salida: TArrayOfString;
begin
  GuardarRespuestas;
  Clave := '';
  if not CorrerAyudante('revisar --respuestas "' + RespuestasArchivo + '"', ExpandConstant('{tmp}\revisar.txt'), Salida) then
  begin
    Texto := 'No se pudieron revisar las respuestas (el ayudante del instalador no corrió): no se cambió nada. Revisa que el antivirus no lo '
      + 'esté deteniendo y vuelve a correr el instalador.';
    Result := False;
    Exit;
  end;
  Texto := Valor(Salida, 'error');
  Clave := Valor(Salida, 'clave');
  Result := Texto = '';
  if Result then
    Log('Las respuestas se pueden usar. Cambiaría: ' + Valor(Salida, 'cambios'))
  else
    Log('La revisión no pasa (' + Clave + '): ' + Texto);
  if Result and (MudanzaElegida = 'traer') then
    Result := RevisarElPaquete(Clave, Texto);
end;

{ La página donde se contesta esa respuesta (la clave de respuestas.txt); wpReady si no es de una página (un archivo de la PC). }
function PaginaDeLaClave(const Clave: String): Integer;
begin
  if Clave = 'rol' then
    Result := PaginaRol.ID
  else if (Clave = 'mudanza') or EmpiezaCon(Clave, 'mudanza-') then
    Result := PaginaMudanza.ID
  else if Clave = 'principal' then
    Result := PaginaPrincipal.ID
  else if (Clave = 'impresora') or (Clave = 'cola') then
    Result := PaginaImpresoras.ID
  else if EmpiezaCon(Clave, 'lector') then
    Result := PaginaLector.ID
  else if EmpiezaCon(Clave, 'controlprint') then
    Result := PaginaControlPrint.ID
  else if (Clave = 'eleventa-gbak') or (Clave = 'eleventa-respaldos') then
    Result := PaginaEleventa2.ID
  else if EmpiezaCon(Clave, 'eleventa') then
    Result := PaginaEleventa.ID
  else
    Result := wpReady;
end;

{ Regresa a esa página, como si se oprimiera «Atrás» las veces que hagan falta. }
procedure IrALaPagina(const Destino: Integer);
var
  Vueltas, Antes: Integer;
begin
  for Vueltas := 1 to 20 do
  begin
    if WizardForm.CurPageID = Destino then
      Exit;
    Antes := WizardForm.CurPageID;
    WizardForm.BackButton.OnClick(WizardForm.BackButton);
    if WizardForm.CurPageID = Antes then
      Exit;
  end;
end;

{ resultado.txt cuando no se cambió nada porque algo no se podía usar (sin preguntar, sale con 7): para quien lo revise (el CI). }
procedure GuardarResultadoSinCambios(const Motivo: String);
var
  Lineas: TArrayOfString;
  Texto: String;
begin
  Texto := Motivo;
  StringChangeEx(Texto, #13#10, ' ', True);
  Agregar(Lineas, 'resultado', 'fallo');
  Agregar(Lineas, 'codigo', '7');
  Agregar(Lineas, 'rol', RolElegido);
  Agregar(Lineas, 'version', VersionDelInstalador);
  Agregar(Lineas, 'programa', 'sin-cambios');
  Agregar(Lineas, 'motivo', Texto);
  ForceDirectories(ExpandConstant('{commonappdata}\PapeleriaPos\instalador'));
  SaveStringsToUTF8File(ExpandConstant('{commonappdata}\PapeleriaPos\instalador\resultado.txt'), Lineas, False);
end;

{ instalar-pc.ps1 con ese paso; lo que dijo queda en Salida. Devuelve su código de salida (-1 si no corrió). }
function CorrerPaso(const Paso, Mas: String; var Salida: TArrayOfString): Integer;
var
  Archivo: String;
  Codigo: Integer;
begin
  Archivo := ExpandConstant('{tmp}\paso-' + Paso + '.txt');
  DeleteFile(Archivo);
  SetArrayLength(Salida, 0);
  if not Exec(PowerShell, '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + ExpandConstant('{app}\instalador\instalar-pc.ps1')
    + '" -Paso ' + Paso + ' -Respuestas "' + RespuestasArchivo + '" -Paquete "' + ExpandConstant('{tmp}\paquete') + '" -Ayudante "' + Ayudante
    + '" -Registro "' + Registro + '" -Resultado "' + Archivo + '"' + Mas, ExpandConstant('{tmp}'), SW_HIDE, ewWaitUntilTerminated, Codigo) then
    Codigo := -1;
  if not LoadStringsFromFile(Archivo, Salida) then
    SetArrayLength(Salida, 0);
  Log('instalar-pc.ps1 -Paso ' + Paso + ' terminó con ' + IntToStr(Codigo));
  Result := Codigo;
end;

{ La clave de eleventa y el código de Control Print, directo de su casilla a las variables del servicio. Vacíos, se quedan los que tiene. }
function PonerSecretos: Boolean;
begin
  Result := False;
  ClaveDice := 'no-aplica';
  CodigoDice := 'no-aplica';
  if ElSi.Checked then
  begin
    if EditClave.Text <> '' then
    begin
      if PonerVariable('Eleventa__Clave', EditClave.Text) then
      begin
        ClaveDice := 'puesta';
        Result := True;
      end
      else
        ClaveDice := 'no-se-pudo';
    end
    else if TieneVariable('Eleventa__Clave') then
      ClaveDice := 'se-quedo'
    else
      ClaveDice := 'no-hay';
  end;
  EditClave.Text := '';
  if CpSi.Checked then
  begin
    if EditCodigo.Text <> '' then
    begin
      if PonerVariable('ControlPrint__Codigo', CodigoNormal(EditCodigo.Text)) then
      begin
        CodigoDice := 'puesto';
        Result := True;
      end
      else
        CodigoDice := 'no-se-pudo';
    end
    else if TieneVariable('ControlPrint__Codigo') then
      CodigoDice := 'se-quedo'
    else
      CodigoDice := 'no-hay';
  end
  else if TieneVariable('ControlPrint__Codigo') then
  begin
    { «No» en Control Print: su sección se quita de servicio.json, y su código también. }
    if PonerVariable('ControlPrint__Codigo', '') then
    begin
      CodigoDice := 'quitado';
      Result := True;
    end
    else
      CodigoDice := 'no-se-pudo';
  end;
  EditCodigo.Text := '';
end;

function TextoDe(const Que, Dice, Mensaje: String): String;
begin
  Result := Que + ': ';
  if Dice = 'contesta' then
    Result := Result + 'contesta.'
  else if Dice = 'no-contesta' then
    Result := Result + 'no contestó.'
  else
    Result := Result + Dice + '.';
  if Mensaje <> '' then
    Result := Result + ' ' + Mensaje;
  Result := Result + #13#10;
end;

{ Por qué no se vende en esta caja cuando actualizar.ps1 no pudo regresar solo (sale con 12). }
function PorQueNoVender: String;
begin
  if ConServicio then
    Result := 'El servicio quedó detenido y no se pudo regresar a la versión de antes. Avisa a quien instala. Lo de antes está completo en '
      + 'su respaldo (C:\ProgramData\PapeleriaPos\actualizaciones).'
  else
    Result := 'La caja quedó a medias y no se pudo regresar a la versión de antes. Avisa a quien instala. Lo de antes está completo en su '
      + 'respaldo (C:\ProgramData\PapeleriaPos\actualizaciones).';
end;

{ Lo que dicen «Cómo quedó» y resultado.txt en ese caso; la última página lo dice con su título. }
function NoVender: String;
begin
  Result := 'NO VENDAS EN ESTA CAJA. ' + PorQueNoVender;
end;

{ Por qué no quedó el programa: lo que dijo instalar-pc.ps1, o que no dijo nada. }
function PorQueNoSeInstalo: String;
begin
  Result := Valor(Paso1, 'motivo');
  if Result = '' then
  begin
    if CodigoPaso1 = -1 then
      Result := 'el paso de instalar no corrió (Windows PowerShell no abrió)'
    else
      Result := 'el paso de instalar terminó con ' + IntToStr(CodigoPaso1) + ' sin decir cómo quedó';
  end;
end;

{ Si corrió la revisión de que todo conteste (el paso 2): con su código 0 y lo que dice del servicio. }
function RevisionBien: Boolean;
begin
  Result := (CodigoPaso2 = 0) and (Valor(Paso2, 'servicio') <> '');
end;

function PorQueNoSeReviso: String;
begin
  Result := Valor(Paso2, 'motivo');
  if Result = '' then
  begin
    if CodigoPaso2 = -1 then
      Result := 'no corrió'
    else
      Result := 'terminó con ' + IntToStr(CodigoPaso2) + ' sin decir cómo quedó';
  end;
end;

{ ── Cómo quedó la mudanza de la base ── }

{ Cómo quedó, según instalar-pc.ps1: sacada, traida, ya-estaba, a-medias, fallo, convertida o cancelada; vacío si no hubo mudanza. Lo dice el
  paso de instalar, y el de revisar lo corrige si al final algo no cuadró (una base recién traída cuyo servicio ya no contestó con esa tienda
  queda «a-medias»): vale lo último que se dijo. }
function MudanzaQuedo: String;
begin
  Result := Valor(Paso2, 'mudanza');
  if Result = '' then
    Result := Valor(Paso1, 'mudanza');
end;

{ El porqué, o lo que falta, como lo dijo el script que lo sabe (sus mensajes van sin acentos). }
function MudanzaMensaje: String;
begin
  Result := Valor(Paso2, 'mudanza-mensaje');
  if Result = '' then
    Result := Valor(Paso1, 'mudanza-mensaje');
end;

{ Al sacar, la carpeta donde quedó el paquete; al traer, «borrado» cuando ya se le quitó su base (quedó bien puesta en esta PC). }
function MudanzaPaqueteQuedo: String;
begin
  Result := Valor(Paso2, 'mudanza-paquete');
  if Result = '' then
    Result := Valor(Paso1, 'mudanza-paquete');
end;

{ Si con esa mudanza no conviene abrir la caja al terminar: con la base ya sacada, esta PC ya no atiende (su caja se abre al terminar la
  mudanza); recién traída, la caja de esta PC se da de alta hasta el final, ya terminada la mudanza; y a medias, o sin haberla traído (o sin
  saber si se trajo), abrirla en una caja principal sin su base crearía una tienda nueva. }
function MudanzaSinAbrirLaCaja: Boolean;
var
  Quedo: String;
begin
  Quedo := MudanzaQuedo;
  Result := (Quedo = 'sacada') or (Quedo = 'traida') or (Quedo = 'ya-estaba') or (Quedo = 'a-medias')
    or (((Quedo = 'fallo') or (Quedo = '')) and (MudanzaElegida = 'traer'));
end;

{ Los avisos del paquete como los junta instalar-pc.ps1 (separados con « | »), uno por renglón. }
function AvisosEnRenglones(const Unidos: String): String;
var
  Lista: TArrayOfString;
  I: Integer;
begin
  Result := '';
  Dividir(Unidos, ' | ', Lista);
  for I := 0 to GetArrayLength(Lista) - 1 do
    if Trim(Lista[I]) <> '' then
      Result := Result + '• ' + Trim(Lista[I]) + #13#10;
end;

{ El nombre de la caja de esta PC en la base que se mudó, para decir cuál elegir si al abrirla pide su alta otra vez. Lo dice el mudanza.txt
  de la carpeta donde se vio el traida.txt de este paquete: la caja que usa este equipo, o la única que viene. Vacío si no se sabe. }
function CajaDeEstaPc: String;
var
  Carpeta, Ips, Puerto: String;
  Lineas: TArrayOfString;
  I, N: Integer;
begin
  Result := '';
  if not LaTraida(Carpeta, Ips, Puerto) then
    Exit;
  if not LeerClaves(AddBackslash(Carpeta) + 'mudanza.txt', Lineas) then
    Exit;
  if CompareText(Trim(Valor(Lineas, 'paquete')), Trim(Valor(Actual, 'mudanza-paquete'))) <> 0 then
    Exit;
  N := StrToIntDef(Trim(Valor(Lineas, 'cajas')), 0);
  if N > 50 then
    N := 50;
  for I := 1 to N do
    if CompareText(Trim(Valor(Lineas, 'caja.' + IntToStr(I) + '.equipo')), GetComputerNameString) = 0 then
    begin
      Result := Trim(Valor(Lineas, 'caja.' + IntToStr(I) + '.nombre'));
      Exit;
    end;
  if N = 1 then
    Result := Trim(Valor(Lineas, 'caja.1.nombre'));
end;

{ Lo primero que dice «Cómo quedó» cuando hubo mudanza: cómo quedó la base de la tienda y qué sigue (docs/instalacion.md, «La base de la
  tienda»). Vacío si no hubo. Lo que se pidió y no se supo cómo quedó también se dice: no se da por hecho. }
function ComoQuedoLaMudanza: String;
var
  Quedo, Porque, Equipo, Carpeta, Cajas, Alta, Nombre, Puente, Avisos: String;
begin
  Result := '';
  Quedo := MudanzaQuedo;
  Porque := SinPunto(MudanzaMensaje);
  if Porque <> '' then
    Porque := ': ' + Porque;
  if Quedo = 'sacada' then
  begin
    Carpeta := Valor(Paso1, 'mudanza-paquete');
    if Carpeta = '' then
      Carpeta := CarpetaDelPaquete;
    Result := 'LA BASE DE ESTA PC YA ESTÁ EN EL PAQUETE DE MUDANZA (' + Carpeta + '). Esta PC ya no atiende: su servicio quedó detenido a '
      + 'propósito. Si abres la caja de esta PC, vende con su copia y guarda lo cobrado para subirlo después.' + #13#10
      + 'SIGUE: expulsa la USB, llévala a la PC principal, corre ahí este instalador y elige «Caja principal»; después regresa a esta PC y corre '
      + 'el instalador otra vez para terminar. No hagas el corte en eleventa hasta terminar. Si te arrepientes: corre este instalador aquí y '
      + 'elige «Cancelar la mudanza».' + #13#10;
    { Si el paquete quedó en el disco de esta PC (así se aceptó), la USB no lo lleva: antes hay que copiarlo a ella. }
    if EnElDiscoDeEstaPc(Carpeta) then
      Result := Result + 'OJO: ese paquete quedó en el disco de esta PC, no en la USB. Antes de llevarla, copia esa carpeta a la USB, junto al '
        + 'instalador y con el nombre «mudanza».' + #13#10;
    Result := Result + AvisosEnRenglones(Valor(Paso1, 'mudanza-avisos'));
  end
  else if (Quedo = 'traida') or (Quedo = 'ya-estaba') then
  begin
    Equipo := Trim(Valor(Paso1, 'mudanza-equipo'));
    if Equipo = '' then
      Equipo := Trim(Valor(Paquete, 'equipo'));
    if Equipo = '' then
      Result := 'LA BASE DE LA OTRA PC YA ESTÁ EN ESTA PC. FALTA UN PASO: ve a la otra PC'
    else
      Result := 'LA BASE DE ' + Equipo + ' YA ESTÁ EN ESTA PC. FALTA UN PASO: ve a ' + Equipo;
    Result := Result + ' y corre este instalador (ya viene «Terminar la mudanza»); hasta entonces lo que cobre allá no llega aquí ni a eleventa.'
      + #13#10 + 'La caja de esta PC y el celular se dan de alta DESPUÉS: en el alta de esta PC elige «Caja nueva», no ';
    Cajas := CajasDelPaquete;
    if Cajas = '' then
      Result := Result + 'una de las cajas que venían en la base.' + #13#10
    else
      Result := Result + Cajas + '.' + #13#10;
    Puente := ComoLlegaElPuente('llegó');
    if Puente <> '' then
      Result := Result + Puente + #13#10;
    Avisos := AvisosEnRenglones(Valor(Paso1, 'mudanza-avisos'));
    if Avisos = '' then
      Avisos := AvisosDelPaquete;
    Result := Result + Avisos;
  end
  else if Quedo = 'a-medias' then
    Result := 'La base ya está en esta PC, pero no quedó terminado' + Porque + '. No abras la caja. Corre este instalador otra vez aquí, con la '
      + 'misma USB, para terminar. La otra PC sigue detenida: no se pierde nada.' + #13#10
  else if Quedo = 'fallo' then
  begin
    if MudanzaElegida = 'sacar' then
    begin
      { Casi siempre el servicio vuelve a quedar en marcha; si no (lo dice el paso de revisar), no se dice que sigue como antes. }
      Result := 'No se sacó la base' + Porque + '.';
      if Valor(Paso2, 'servicio') = 'contesta' then
        Result := Result + ' Esta PC sigue trabajando como antes.'
      else
        Result := Result + ' OJO: el servicio de esta PC no quedó trabajando como antes (abajo dice cómo quedó): arréglalo antes de vender.';
    end
    else if MudanzaElegida = 'traer' then
      Result := 'No se trajo la base' + Porque + '. No abras la caja de esta PC todavía: no tiene la base que se iba a traer (y si la suya está '
        + 'vacía, abrirla crearía una tienda nueva). La otra PC sigue detenida; puedes repetirlo aquí, o cancelar allá.'
    else if MudanzaElegida = 'cancelar' then
      Result := 'No se canceló la mudanza' + Porque + '.'
    else
      Result := 'No se terminó la mudanza' + Porque + '.';
    Result := Result + #13#10;
  end
  else if Quedo = 'convertida' then
  begin
    Result := 'ESTA PC YA ES «OTRA CAJA» DE ' + Trim(EditPrincipal.Text) + '.';
    Alta := Valor(Paso1, 'mudanza-alta');
    if Alta = 'se-conserva' then
      Result := Result + ' No pide alta ni PIN: ábrela; dirá «Subiendo N…»; espera a «Conectada a la principal» antes del corte.'
    else if Alta = 'otra-vez' then
    begin
      Nombre := CajaDeEstaPc;
      if Nombre <> '' then
        Nombre := ' (' + Nombre + ')';
      Result := Result + ' La principal ya no reconoce la llave de esta caja: al abrirla pedirá darla de alta otra vez; elige la MISMA caja de '
        + 'antes' + Nombre + ', no «Caja nueva».';
    end
    else if Alta = 'no-habia' then
      Result := Result + ' Al abrirla, dala de alta con el usuario y el PIN del dueño.';
    Result := Result + #13#10;
    if Valor(Paso1, 'mudanza-apartada') <> '' then
      Result := Result + 'La base de antes quedó apartada en ' + Valor(Paso1, 'mudanza-apartada') + ', no se borró.' + #13#10;
  end
  else if Quedo = 'cancelada' then
  begin
    Result := 'SE CANCELÓ LA MUDANZA: esta PC vuelve a trabajar con su base.';
    if MudanzaMensaje <> '' then
      Result := Result + ' ' + MudanzaMensaje;
    Result := Result + #13#10;
  end
  else if Quedo <> '' then
    Result := 'La mudanza: ' + Quedo + Porque + '.' + #13#10
  else if ProgramaBien and (MudanzaElegida <> '') then
    Result := 'OJO: no se supo cómo quedó la mudanza de la base (instalar-pc.ps1 no lo dijo). Antes de seguir, revisa el detalle.' + #13#10;
end;

{ La última página cuando hubo mudanza y el programa quedó: su título y lo que sigue, en corto («Cómo quedó» lo dice completo). Falso si no hubo
  mudanza: ahí va lo de siempre. }
function UltimaPaginaDeLaMudanza(var Titulo, Texto: String): Boolean;
var
  Quedo, Equipo, Cajas, Alta, Nombre, Carpeta: String;
begin
  Result := True;
  Quedo := MudanzaQuedo;
  if Quedo = 'sacada' then
  begin
    Titulo := 'La base ya está en el paquete';
    Carpeta := Valor(Paso1, 'mudanza-paquete');
    if Carpeta = '' then
      Carpeta := CarpetaDelPaquete;
    { Si el paquete quedó en el disco de esta PC (así se aceptó), la USB no lo lleva: antes hay que copiarlo a ella. }
    if EnElDiscoDeEstaPc(Carpeta) then
      Texto := 'El paquete quedó en el disco de esta PC: cópialo a la USB («Cómo quedó», la página de antes, dice cómo), llévala'
    else
      Texto := 'Expulsa la USB, llévala';
    Texto := 'Esta PC ya no atiende: su servicio quedó detenido a propósito. ' + Texto + ' a la PC principal y corre ahí este '
      + 'instalador («Caja principal»); después regresa aquí y córrelo otra vez para terminar.' + #13#10#13#10
      + 'Si abres la caja de esta PC, vende con su copia y lo sube después. No hagas el corte en eleventa hasta terminar.';
  end
  else if (Quedo = 'traida') or (Quedo = 'ya-estaba') then
  begin
    Equipo := Trim(Valor(Paso1, 'mudanza-equipo'));
    if Equipo = '' then
      Equipo := EquipoDelPaquete;
    Cajas := CajasDelPaquete;
    if Cajas = '' then
      Cajas := 'una de las cajas que venían en la base';
    Titulo := 'La base ya está aquí: falta un paso';
    Texto := 'Ve a ' + Equipo + ' y corre ahí este instalador (ya viene «Terminar la mudanza»); hasta entonces lo que cobre allá no llega aquí ni '
      + 'a eleventa.' + #13#10#13#10
      + 'La caja de esta PC y el celular se dan de alta después, ya terminada la mudanza: en el alta de esta PC elige «Caja nueva», no ' + Cajas + '.';
  end
  else if Quedo = 'a-medias' then
  begin
    Titulo := 'La mudanza quedó a medias';
    Texto := 'La base ya está en esta PC, pero no quedó terminado (el porqué está en «Cómo quedó», la página de antes). No abras la caja: corre '
      + 'este instalador otra vez aquí, con la misma USB, para terminar.' + #13#10#13#10 + 'La otra PC sigue detenida: no se pierde nada.';
  end
  else if (Quedo = 'fallo') and (MudanzaElegida = 'traer') then
  begin
    Titulo := 'No se trajo la base';
    Texto := 'Papelería POS quedó instalada, pero la base de la otra PC no se trajo (el porqué está en «Cómo quedó», la página de antes). No abras '
      + 'la caja de esta PC todavía: si su base está vacía, crearía una tienda nueva.' + #13#10#13#10
      + 'La otra PC sigue detenida: puedes repetirlo aquí, o cancelar allá.';
  end
  else if (Quedo = 'fallo') and (MudanzaElegida = 'sacar') then
  begin
    Titulo := 'No se sacó la base';
    if Valor(Paso2, 'servicio') = 'contesta' then
      Texto := 'Esta PC sigue trabajando como antes, con su base (el porqué está en «Cómo quedó», la página de antes). Para mudar la base, corre '
        + 'este instalador otra vez.'
    else
      Texto := 'Y el servicio de esta PC no quedó trabajando como antes: antes de vender, lee «Cómo quedó» (la página de antes), que dice por qué y '
        + 'qué sigue. El detalle está en C:\ProgramData\PapeleriaPos\instalador.';
  end
  else if Quedo = 'convertida' then
  begin
    Titulo := 'Esta PC ya es «Otra caja»';
    Texto := 'Se conecta a ' + Trim(EditPrincipal.Text) + '. ';
    Alta := Valor(Paso1, 'mudanza-alta');
    if Alta = 'se-conserva' then
      Texto := Texto + 'Abre la caja: no pide alta ni PIN. Dirá «Subiendo N…»; espera a «Conectada a la principal» antes del corte.'
    else if Alta = 'otra-vez' then
    begin
      Nombre := CajaDeEstaPc;
      if Nombre <> '' then
        Nombre := ' (' + Nombre + ')';
      Texto := Texto + 'Al abrir la caja pedirá darla de alta otra vez: elige la MISMA caja de antes' + Nombre + ', no «Caja nueva».';
    end
    else if Alta = 'no-habia' then
      Texto := Texto + 'Abre la caja y dala de alta con el usuario y el PIN del dueño.'
    else
      Texto := Texto + 'Abre la caja para terminar.';
    Texto := Texto + #13#10#13#10 + 'La base de antes quedó apartada en esta PC, no se borró («Cómo quedó» dice dónde).';
  end
  else if Quedo = 'cancelada' then
  begin
    Titulo := 'Se canceló la mudanza';
    Texto := 'Esta PC vuelve a trabajar con su base, como antes: abre la caja para seguir.';
    if MudanzaMensaje <> '' then
      Texto := Texto + #13#10#13#10 + MudanzaMensaje;
  end
  else if (Quedo <> '') or (MudanzaElegida <> '') then
  begin
    { Lo que se pidió y no se supo cómo quedó, o no quedó (y no era traer ni sacar): no se da por hecho. }
    Titulo := 'Revisa cómo quedó la mudanza';
    Texto := 'Papelería POS quedó instalada, pero lo de la mudanza de la base no quedó, o no se supo cómo quedó. Antes de abrir la caja, lee «Cómo '
      + 'quedó» (la página de antes); el detalle está en C:\ProgramData\PapeleriaPos\instalador.';
  end
  else
    Result := False;
end;

{ Lo que se enseña en «Cómo quedó», y C:\ProgramData\PapeleriaPos\instalador\resultado.txt para quien lo revise (el CI). }
procedure Terminar;
var
  Texto, Programa, General: String;
  Lineas: TArrayOfString;
begin
  Programa := Valor(Paso1, 'programa');
  if not ProgramaBien then
  begin
    if CodigoPaso1 = 2 then
    begin
      CodigoDeSalida := 12;
      General := 'no-regreso';
    end
    else
    begin
      CodigoDeSalida := 11;
      General := 'fallo';
    end;
  end
  else if (WebView2Dice = 'fallo') or not RevisionBien or (Valor(Paso2, 'servicio') = 'no-contesta') or (Valor(Paso2, 'eleventa') = 'no-contesta')
    or (Valor(Paso2, 'controlprint') = 'no-contesta') or (Valor(Paso2, 'principal') = 'no-contesta')
    or (Valor(Paso1, 'cola') = 'no-se-pudo') or (ClaveDice = 'no-se-pudo') or (CodigoDice = 'no-se-pudo')
    { El programa quedó y la mudanza no (o quedó a medias, o se pidió y no se supo cómo quedó): también es «con avisos». }
    or (MudanzaQuedo = 'fallo') or (MudanzaQuedo = 'a-medias') or ((MudanzaElegida <> '') and (MudanzaQuedo = '')) then
  begin
    CodigoDeSalida := 10;
    General := 'con-avisos';
  end
  else
  begin
    CodigoDeSalida := 0;
    General := 'bien';
  end;

  Agregar(Lineas, 'resultado', General);
  Agregar(Lineas, 'codigo', IntToStr(CodigoDeSalida));
  Agregar(Lineas, 'rol', RolElegido);
  Agregar(Lineas, 'version', Valor(Paso1, 'version'));
  Agregar(Lineas, 'version-antes', Valor(Paso1, 'version-antes'));
  Agregar(Lineas, 'programa', Programa);
  Agregar(Lineas, 'configuracion', Valor(Paso1, 'configuracion'));
  Agregar(Lineas, 'cambios', Valor(Paso1, 'cambios'));
  Agregar(Lineas, 'cola', Valor(Paso1, 'cola'));
  Agregar(Lineas, 'webview2', WebView2Dice);
  Agregar(Lineas, 'servicio', Valor(Paso2, 'servicio'));
  Agregar(Lineas, 'eleventa', Valor(Paso2, 'eleventa'));
  Agregar(Lineas, 'controlprint', Valor(Paso2, 'controlprint'));
  Agregar(Lineas, 'principal', Valor(Paso2, 'principal'));
  Agregar(Lineas, 'clave-eleventa', ClaveDice);
  Agregar(Lineas, 'codigo-controlprint', CodigoDice);
  { La mudanza de la base, solo si se pidió una o el script dijo una: sin mudanza, este archivo queda como siempre. }
  if (MudanzaElegida <> '') or (MudanzaQuedo <> '') then
  begin
    Agregar(Lineas, 'mudanza', MudanzaQuedo);
    Agregar(Lineas, 'mudanza-mensaje', MudanzaMensaje);
    Agregar(Lineas, 'mudanza-paquete', MudanzaPaqueteQuedo);
    Agregar(Lineas, 'mudanza-alta', Valor(Paso1, 'mudanza-alta'));
  end;
  if ProgramaBien then
    Agregar(Lineas, 'motivo', Valor(Paso1, 'motivo'))
  else
    Agregar(Lineas, 'motivo', PorQueNoSeInstalo);
  if CodigoPaso1 = 2 then
    Agregar(Lineas, 'aviso', NoVender);
  if ProgramaBien then
  begin
    if RevisionBien then
      Agregar(Lineas, 'revision', 'bien')
    else
    begin
      Agregar(Lineas, 'revision', 'fallo');
      Agregar(Lineas, 'revision-motivo', PorQueNoSeReviso);
    end;
  end;
  Agregar(Lineas, 'registro', Registro);
  SaveStringsToUTF8File(ExpandConstant('{commonappdata}\PapeleriaPos\instalador\resultado.txt'), Lineas, False);

  { Lo de la mudanza va primero: es lo que dice qué sigue (llevar la USB, ir a la otra PC, no abrir la caja). }
  Texto := ComoQuedoLaMudanza;
  if Texto <> '' then
    Texto := Texto + #13#10;
  Texto := Texto + 'Esta PC: ' + NombreDelRol(RolElegido) + '.' + #13#10;
  if CodigoPaso1 = 2 then
    Texto := Texto + NoVender + #13#10 + 'Qué pasó: ' + PorQueNoSeInstalo + #13#10
  else if not ProgramaBien then
  begin
    Texto := Texto + 'NO SE TERMINÓ DE INSTALAR: ' + PorQueNoSeInstalo + #13#10;
    { Si el script alcanzó a decir cómo quedó la mudanza antes de fallar, eso sí cambió (lo dice arriba): no todo sigue como estaba. }
    if MudanzaQuedo = '' then
      Texto := Texto + 'Lo de antes sigue como estaba (si se estaba actualizando, regresó a la versión de antes con su respaldo).' + #13#10
    else
      Texto := Texto + 'Lo de la mudanza quedó como dice arriba; lo demás sigue como estaba (si se estaba actualizando, regresó a la versión de '
        + 'antes con su respaldo).' + #13#10;
  end
  else if Programa = 'instalado' then
    Texto := Texto + 'Se instaló Papelería POS ' + Valor(Paso1, 'version') + '.' + #13#10
  else if Programa = 'actualizado' then
    Texto := Texto + 'Se actualizó de ' + Valor(Paso1, 'version-antes') + ' a ' + Valor(Paso1, 'version')
      + ', con el respaldo de antes en C:\ProgramData\PapeleriaPos\actualizaciones.' + #13#10
  else
    Texto := Texto + 'Ya tenía esta versión (' + Valor(Paso1, 'version') + '): solo cambió lo que se contestó distinto.' + #13#10;
  if ProgramaBien then
  begin
    if Valor(Paso1, 'configuracion') = 'cambio' then
      Texto := Texto + 'La configuración de esta PC: ' + Valor(Paso1, 'cambios') + ' (lo de antes, en servicio.json.anterior y caja.json.anterior).' + #13#10
    else
      Texto := Texto + 'La configuración de esta PC: sin cambios.' + #13#10;
    if Valor(Paso1, 'cola') = 'no-se-pudo' then
      Texto := Texto + Valor(Paso1, 'cola-mensaje') + #13#10;
  end;
  if WebView2Dice = 'instalado' then
    Texto := Texto + 'WebView2 (para la pantalla de la caja): se instaló.' + #13#10
  else if WebView2Dice = 'fallo' then
    Texto := Texto + 'WebView2 (para la pantalla de la caja): NO se pudo instalar. Conecta esta PC a internet y corre el instalador otra vez, o '
      + 'instala «Microsoft Edge WebView2 Runtime» de la página de Microsoft.' + #13#10;
  if ProgramaBien and not RevisionBien then
    Texto := Texto + 'No se pudo revisar que todo conteste (' + PorQueNoSeReviso + '): revisa en Servicios que «Papelería POS» esté en '
      + 'ejecución, o corre el instalador otra vez (con las mismas respuestas no cambia nada).' + #13#10
  else if ProgramaBien then
  begin
    if ConServicio then
    begin
      { Con la marca de la mudanza el servicio queda detenido a propósito («mudada»), y eleventa y Control Print no se revisan («no-aplica»). }
      if Valor(Paso2, 'servicio') = 'mudada' then
        Texto := Texto + 'El servicio: detenido a propósito por la mudanza de la base (no arranca hasta terminarla o cancelarla).' + #13#10
      else
        Texto := Texto + TextoDe('El servicio', Valor(Paso2, 'servicio'), Valor(Paso2, 'servicio-mensaje'));
      if ElSi.Checked then
      begin
        if Valor(Paso2, 'eleventa') <> 'no-aplica' then
          Texto := Texto + TextoDe('eleventa', Valor(Paso2, 'eleventa'), Valor(Paso2, 'eleventa-mensaje'));
        if ClaveDice = 'puesta' then
          Texto := Texto + 'La clave de Firebird quedó en el servicio.' + #13#10
        else if ClaveDice = 'no-hay' then
          Texto := Texto + 'Falta la clave de Firebird: sin ella la caja no lee eleventa.' + #13#10
        else if ClaveDice = 'no-se-pudo' then
          Texto := Texto + 'La clave de Firebird NO se pudo guardar en el servicio.' + #13#10;
      end;
      if CpSi.Checked then
      begin
        if Valor(Paso2, 'controlprint') <> 'no-aplica' then
          Texto := Texto + TextoDe('Control Print', Valor(Paso2, 'controlprint'), Valor(Paso2, 'controlprint-mensaje'));
        if CodigoDice = 'puesto' then
          Texto := Texto + 'El código de Control Print quedó en el servicio.' + #13#10
        else if CodigoDice = 'no-hay' then
          Texto := Texto + 'Falta el código de Control Print: sin él no contesta.' + #13#10
        else if CodigoDice = 'no-se-pudo' then
          Texto := Texto + 'El código de Control Print NO se pudo guardar en el servicio.' + #13#10;
      end
      else if CodigoDice = 'quitado' then
        Texto := Texto + 'Control Print: se quitó (su sección y su código).' + #13#10;
    end
    else if Valor(Paso2, 'principal') <> 'no-aplica' then
      Texto := Texto + TextoDe('La caja principal', Valor(Paso2, 'principal'), Valor(Paso2, 'principal-mensaje'));
  end;
  Texto := Texto + #13#10 + 'El detalle: ' + Registro;
  MemoResultado.Text := Texto;
  Log('Cómo quedó: ' + General + ' (código ' + IntToStr(CodigoDeSalida) + ').');
end;

procedure Instalar;
var
  Mas, Elegida: String;
begin
  ClaveDice := 'no-aplica';
  CodigoDice := 'no-aplica';
  WizardForm.ProgressGauge.Style := npbstMarquee;
  Registro := ExpandConstant('{commonappdata}\PapeleriaPos\instalador\instalador-') + GetDateTimeString('yyyy/mm/dd_hh:nn:ss', '-', '.') + '.log';
  ForceDirectories(ExpandConstant('{commonappdata}\PapeleriaPos\instalador'));
  RevisarWebView2;
  GuardarRespuestas;
  { Con una mudanza este paso tarda más (sacar o poner la base entera), y de la USB depende: que se sepa mientras se espera. }
  Elegida := MudanzaElegida;
  if Elegida = 'sacar' then
    Estado('Sacando la base al paquete de mudanza (unos minutos): no quites la USB…')
  else if Elegida = 'traer' then
    Estado('Trayendo la base del paquete de mudanza (varios minutos): no quites la USB…')
  else if Elegida = 'terminar' then
    Estado('Terminando la mudanza: esta PC queda como «Otra caja» (unos minutos)…')
  else if Elegida = 'cancelar' then
    Estado('Cancelando la mudanza: esta PC vuelve a trabajar con su base (unos minutos)…')
  else
    Estado('Instalando el programa: si ya estaba, primero se respalda la base (unos minutos)…');
  CodigoPaso1 := CorrerPaso('instalar', '', Paso1);
  { Con 0 y sin decir cómo quedó (su resultado vacío) tampoco es «bien»: no se sabe qué hizo. }
  ProgramaBien := (CodigoPaso1 = 0) and (Valor(Paso1, 'programa') <> '');
  SetArrayLength(Paso2, 0);
  CodigoPaso2 := 0;
  if ProgramaBien then
  begin
    Mas := '';
    if ConServicio then
      if PonerSecretos then
        Mas := ' -Reiniciar';
    if Valor(Paso1, 'configuracion') = 'cambio' then
      Mas := ' -Reiniciar';
    { Con la base recién sacada el servicio queda detenido a propósito: no se pide reiniciarlo, aunque se haya puesto una clave. }
    if Valor(Paso1, 'mudanza') = 'sacada' then
      Mas := '';
    Estado('Revisando que todo conteste (eleventa puede tardar un par de minutos)…');
    CodigoPaso2 := CorrerPaso('revisar', Mas, Paso2);
  end;
  Terminar;
  WizardForm.ProgressGauge.Style := npbstNormal;
end;

{ ── Lo que llama Inno ── }

function InitializeSetup: Boolean;
var
  Codigo: Integer;
  Archivo, Instalada: String;
begin
  Result := True;
  { Antes de que Inno cree su SetupMutex: si ya está, es otra copia (en esta cuenta o en otra). }
  if CheckForMutexes(MutexDelInstalador) then
  begin
    Avisar('El instalador de Papelería POS ya se está corriendo en esta PC (quizá en otra ventana, o en otra cuenta de Windows): espera a '
      + 'que termine y, si hace falta, vuelve a abrirlo.', mbError);
    Result := False;
    Exit;
  end;
  ExtractTemporaryFile('PapeleriaPos.Instalacion.exe');
  Archivo := ExpandConstant('{tmp}\actual.txt');
  if not Exec(Ayudante, 'leer --salida "' + Archivo + '"', ExpandConstant('{tmp}'), SW_HIDE, ewWaitUntilTerminated, Codigo) then
    Codigo := -1;
  if not LoadStringsFromFile(Archivo, Actual) then
    SetArrayLength(Actual, 0);
  Log('El ayudante (leer) terminó con ' + IntToStr(Codigo));
  HayPos := (Valor(Actual, 'servicio-instalado') = 'si') or (Valor(Actual, 'caja-instalada') = 'si')
    or RegKeyExists(HKLM, ClaveDelServicio) or FileExists(ExpandConstant('{commonpf64}\PapeleriaPos\Caja\PapeleriaPos.Caja.exe'));
  if Codigo = 2 then
  begin
    Avisar('No se pudo leer la configuración de esta PC: ' + Valor(Actual, 'error'), mbError);
    Result := False;
  end
  else if (Codigo <> 0) and HayPos then
  begin
    { Sin lo que ya tiene, las respuestas saldrían vacías y se pisaría su configuración: mejor no seguir. }
    Avisar('No se pudo revisar lo que ya tiene esta PC (el ayudante del instalador no corrió): no se cambió nada. Revisa que el antivirus no lo '
      + 'esté deteniendo y vuelve a correr el instalador.', mbError);
    Result := False;
  end
  else
  begin
    Instalada := Valor(Actual, 'servicio-version');
    if Instalada = '' then
      Instalada := Valor(Actual, 'caja-version');
    if (Instalada <> '') and (CompararVersiones(Instalada, VersionDelInstalador) > 0) then
    begin
      Avisar('En esta PC ya está Papelería POS ' + Instalada + ', más nueva que la de este instalador (' + VersionDelInstalador + '): no se instala '
        + 'una anterior. Usa el instalador de la ' + Instalada + ' o una más nueva.', mbError);
      Result := False;
    end;
  end;
end;

procedure InitializeWizard;
var
  Instalada, Como: String;
begin
  CrearPaginas;
  Prellenar;
  AplicarParametros;
  ActualizarImpresoras;
  ActualizarLector;
  ActualizarControlPrint;
  ActualizarEleventa;
  ActualizarGbak;
  WizardForm.WelcomeLabel1.Caption := 'Instalar Papelería POS ' + VersionDelInstalador;
  if HayPos then
  begin
    Instalada := Valor(Actual, 'servicio-version');
    if Instalada = '' then
      Instalada := Valor(Actual, 'caja-version');
    if RegKeyExists(HKLM, ClaveDesinstalar) then
      Como := 'con este instalador'
    else
      Como := 'a mano, con los scripts';
    if CompararVersiones(Instalada, VersionDelInstalador) = 0 then
      WizardForm.WelcomeLabel2.Caption := 'En esta PC ya está Papelería POS ' + Instalada + ' (se instaló ' + Como + '): es la misma versión, así que '
        + 'solo cambia lo que contestes distinto. Las respuestas ya vienen con lo que tiene.' + #13#10#13#10
        + 'Antes de seguir, cierra la caja en esta PC.'
    else
      WizardForm.WelcomeLabel2.Caption := 'En esta PC ya está Papelería POS ' + Instalada + ' (se instaló ' + Como + '). Se actualiza a la '
        + VersionDelInstalador + ' sin perder la base, la configuración ni el alta de la caja: antes se respaldan, y si la nueva no arranca, '
        + 'regresa sola a la de antes. Las respuestas ya vienen con lo que tiene: cambia solo lo que quieras.' + #13#10#13#10
        + 'Antes de seguir, cierra la caja en esta PC.';
  end
  else
    WizardForm.WelcomeLabel2.Caption := 'Esto instala Papelería POS en esta PC: la caja y, en la caja principal (o en la única caja), el servicio '
      + 'con la base de la tienda. Te hace unas preguntas sencillas; cada página dice para qué es.' + #13#10#13#10
      + 'Al terminar se abre la caja, y ahí se termina: la tienda, la cuenta del dueño y los costos de impresión.';
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := False;
  { «La base de la tienda» sale con la base ya sacada (ahí se termina o se cancela) y, sin eso, solo si lo marcado lleva servicio. }
  if PageID = PaginaMudanza.ID then
    Result := not HayMarca and (RolMarcado <> 'principal') and (RolMarcado <> 'unica')
  else if PageID = PaginaPrincipal.ID then
    Result := RolElegido <> 'otra'
  else if (PageID = PaginaControlPrint.ID) or (PageID = PaginaEleventa.ID) then
    Result := not ConServicio
  else if PageID = PaginaEleventa2.ID then
    Result := not ConServicio or not ElSi.Checked;
end;

{ Lo que revisa «Siguiente» en cada página y, en «Listo para instalar», lo que se revisa antes de cambiar nada. }
function RevisarLaPagina(const Pagina: Integer): Boolean;
var
  Clave, Texto, Antes: String;
  Destino: Integer;
  Bien: Boolean;
begin
  Result := True;
  if Pagina = PaginaRol.ID then
    Result := RevisarRol
  else if Pagina = PaginaMudanza.ID then
    Result := RevisarMudanza
  else if Pagina = PaginaPrincipal.ID then
    Result := RevisarPrincipal
  else if Pagina = PaginaImpresoras.ID then
    Result := RevisarImpresoras
  else if Pagina = PaginaLector.ID then
    Result := RevisarLector
  else if Pagina = PaginaControlPrint.ID then
    Result := RevisarControlPrint
  else if Pagina = PaginaEleventa.ID then
    Result := RevisarEleventa
  else if Pagina = PaginaEleventa2.ID then
    Result := RevisarEleventa2
  else if (Pagina = wpReady) and not WizardSilent then
  begin
    { La caja se cierra antes de tocar nada: así no se pierde la venta que se atiende ni pisa su configuración al cerrarse. }
    while CajaAbierta do
      if MsgBox('La caja está abierta en esta PC. Cobra o deja en espera la venta que se esté atendiendo, sal con tu usuario y ciérrala '
        + '(los tickets en espera se guardan).' + #13#10#13#10 + 'Después oprime Reintentar.', mbInformation, MB_RETRYCANCEL) <> IDRETRY then
      begin
        Result := False;
        Exit;
      end;
    { Antes de cambiar nada: si una respuesta no se puede usar, se dice y se regresa a su página. Con una base por traer la revisión lee su
      paquete entero y puede tardar: que se vea que está en eso. }
    Antes := WizardForm.PageDescriptionLabel.Caption;
    if MudanzaElegida = 'traer' then
      WizardForm.PageDescriptionLabel.Caption := 'Revisando el paquete de mudanza antes de cambiar nada (puede tardar un minuto)…';
    try
      Bien := RevisarConElAyudante(Clave, Texto);
    finally
      WizardForm.PageDescriptionLabel.Caption := Antes;
    end;
    if not Bien then
    begin
      Destino := PaginaDeLaClave(Clave);
      if Destino <> wpReady then
        Texto := Texto + #13#10#13#10 + 'No se cambió nada en esta PC. Te regreso a la página «' + PageFromID(Destino).Caption
          + '» para corregirlo.'
      else
        Texto := Texto + #13#10#13#10 + 'No se cambió nada en esta PC.';
      MsgBox(Texto, mbError, MB_OK);
      IrALaPagina(Destino);
      Result := False;
    end;
  end;
end;

{ Las revisiones corren el ayudante, y mientras corre el asistente sigue atendiendo sus botones: un segundo «Siguiente» (o «Instalar») a media
  revisión no cuenta. Sin esto se revisaría dos veces a la vez y, al terminar las dos, se brincaría una página (con la mudanza hay revisiones
  que tardan: leer el paquete entero, probar la dirección de la caja principal). }
function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := False;
  if Ocupado then
    Exit;
  Ocupado := True;
  try
    Result := RevisarLaPagina(CurPageID);
  finally
    Ocupado := False;
  end;
end;

function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo, MemoTypeInfo, MemoComponentsInfo, MemoGroupInfo, MemoTasksInfo: String): String;
var
  Texto, Instalada, Elegida: String;
begin
  Texto := 'Esta PC: ' + NombreDelRol(RolElegido) + NewLine;
  Instalada := Valor(Actual, 'servicio-version');
  if Instalada = '' then
    Instalada := Valor(Actual, 'caja-version');
  if HayPos and (CompararVersiones(Instalada, VersionDelInstalador) = 0) then
    Texto := Texto + Space + 'Ya tiene la ' + VersionDelInstalador + ': solo cambia lo que contestaste distinto.' + NewLine
  else if HayPos then
    Texto := Texto + Space + 'Se actualiza a la ' + VersionDelInstalador + ' sin perder la base, la configuración ni el alta de la caja.' + NewLine
  else
    Texto := Texto + Space + 'Se instala Papelería POS ' + VersionDelInstalador + '.' + NewLine;
  { La mudanza de la base, si se eligió una: lo que va a pasar con la base de la tienda. Este cuadro no parte los renglones (lo que no cabe
    queda escondido a la derecha): lo largo va en dos. }
  Elegida := MudanzaElegida;
  if Elegida = 'sacar' then
    Texto := Texto + Space + 'La base de la tienda: se MUDA a otra PC;' + NewLine
      + Space + 'el paquete queda en ' + CarpetaDelPaquete + ' y esta PC deja de atender.' + NewLine
  else if Elegida = 'traer' then
  begin
    Texto := Texto + Space + 'La base de la tienda: se TRAE la de ' + EquipoDelPaquete;
    if Valor(Paquete, 'hecho-local') <> '' then
      Texto := Texto + ' (paquete de ' + Valor(Paquete, 'hecho-local') + ')';
    Texto := Texto + '.' + NewLine;
  end
  else if Elegida = 'terminar' then
    Texto := Texto + Space + 'La base de la tienda: se TERMINA la mudanza' + NewLine
      + Space + '(esta PC queda como Otra caja de ' + Trim(EditPrincipal.Text) + ').' + NewLine
  else if Elegida = 'cancelar' then
    Texto := Texto + Space + 'La base de la tienda: se CANCELA la mudanza.' + NewLine;
  if RolElegido = 'otra' then
  begin
    if Trim(EditPrincipal.Text) = '' then
      Texto := Texto + Space + 'La caja principal: la busca la caja al abrirla.' + NewLine
    else
      Texto := Texto + Space + 'La caja principal: ' + Trim(EditPrincipal.Text) + NewLine;
  end;
  Texto := Texto + NewLine + 'La caja' + NewLine;
  if ValorTicket = '' then
    Texto := Texto + Space + 'El ticket: en pantalla, sin imprimir.' + NewLine
  else
    Texto := Texto + Space + 'El ticket: ' + ValorTicket + NewLine;
  if ValorCola = '' then
    Texto := Texto + Space + 'La impresora que mide: ninguna.' + NewLine
  else
    Texto := Texto + Space + 'La impresora que mide: ' + ValorCola + ' (con «Conservar documentos impresos»).' + NewLine;
  if LectorCom.Checked then
    Texto := Texto + Space + 'El lector: por ' + Uppercase(Trim(ComboPuerto.Text)) + ' a ' + Trim(ComboBaudios.Text) + ' baudios.' + NewLine
  else
    Texto := Texto + Space + 'El lector: como teclado.' + NewLine;
  if ConServicio then
  begin
    Texto := Texto + NewLine + 'Control Print' + NewLine;
    if CpSi.Checked then
    begin
      Texto := Texto + Space + 'Sí, en ' + Trim(EditCpServidor.Text) + ' (solo se lee).' + NewLine;
      if EditCodigo.Text <> '' then
        Texto := Texto + Space + 'El código: se pone el nuevo en el servicio.' + NewLine
      else if HabiaCodigo then
        Texto := Texto + Space + 'El código: se queda el que tiene.' + NewLine
      else
        Texto := Texto + Space + 'El código: falta.' + NewLine;
    end
    else if HabiaCodigo or (Valor(Actual, 'controlprint') = 'si') then
      Texto := Texto + Space + 'No: se quitan su sección y su código.' + NewLine
    else
      Texto := Texto + Space + 'No.' + NewLine;
    Texto := Texto + NewLine + 'eleventa' + NewLine;
    if ElSi.Checked then
    begin
      if Valor(Actual, 'eleventa-escribe') = 'si' then
        Texto := Texto + Space + 'Sí: lee ' + Trim(EditElBase.Text) + ' en ' + Trim(EditElServidor.Text) + ' y le escribe cada venta (ya lo hacía).' + NewLine
      else if (Elegida = 'traer') and PuenteMudado then
      begin
        { El puente llega como estaba en la PC de donde viene la base: si allá escribía, aquí también (no lo prende este instalador). En su
          propio renglón, para que no quede escondido a la derecha. }
        Texto := Texto + Space + 'Sí: lee ' + Trim(EditElBase.Text) + ' en ' + Trim(EditElServidor.Text) + NewLine;
        if PuenteMudadoEscribe then
          Texto := Texto + Space + 'y le escribe cada venta (así llega el puente con la base que se trae).' + NewLine
        else
          Texto := Texto + Space + 'sin escribirle (así llega el puente con la base que se trae).' + NewLine;
      end
      else
        Texto := Texto + Space + 'Sí: lee ' + Trim(EditElBase.Text) + ' en ' + Trim(EditElServidor.Text) + ' (sin escribirle).' + NewLine;
      if Trim(EditElCaja.Text) <> '' then
        Texto := Texto + Space + 'La caja ' + Trim(EditElCaja.Text) + ' de eleventa; el cajero ' + Trim(EditElCajero.Text) + '.' + NewLine;
      if Trim(ComboGbak.Text) <> '' then
        Texto := Texto + Space + 'Se respalda con ' + Trim(ComboGbak.Text) + ' en ' + Trim(EditRespaldos.Text) + '.' + NewLine;
      if EditClave.Text <> '' then
        Texto := Texto + Space + 'La clave: se pone la nueva en el servicio.' + NewLine
      else if HabiaClave then
        Texto := Texto + Space + 'La clave: se queda la que tiene.' + NewLine
      else
        Texto := Texto + Space + 'La clave: falta.' + NewLine;
    end
    else if Valor(Actual, 'eleventa-seccion') = 'si' then
      Texto := Texto + Space + 'No: lo que tiene servicio.json de eleventa se queda como está.' + NewLine
    else
      Texto := Texto + Space + 'No.' + NewLine;
  end;
  Result := Texto;
end;

{ Sin preguntar, lo que con el asistente a la vista revisa «Instalar» (NextButtonClick): si algo no se puede usar, termina aquí sin cambiar
  nada (sale con 7) y lo dice en resultado.txt. También una mudanza pedida con /MUDANZA= que no aplica a esta PC: no se ignora. }
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Clave, Texto: String;
begin
  Result := '';
  if not WizardSilent then
    Exit;
  { Primero lo de la mudanza: si lo pedido no aplica, lo que hayan dicho las páginas ya no viene al caso. }
  if MudanzaQueNoAplica <> '' then
    Result := MudanzaQueNoAplica
  else if FalloSinPreguntar <> '' then
    Result := FalloSinPreguntar
  else if not RevisarConElAyudante(Clave, Texto) then
    Result := Texto
  else if (Lowercase(Parametro('CERRARCAJA')) <> 'si') and CajaAbierta then
    Result := 'La caja está abierta en esta PC: ciérrala y vuelve a correr el instalador (o, sin preguntar, con /CERRARCAJA=si).';
  if Result <> '' then
  begin
    Log('Se detiene antes de cambiar nada: ' + Result);
    GuardarResultadoSinCambios(Result);
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    Instalar;
end;

procedure CurPageChanged(CurPageID: Integer);
var
  Texto, Titulo, Ips: String;
begin
  if CurPageID = PaginaMudanza.ID then
    AlLlegarALaMudanza
  else if CurPageID = PaginaPrincipal.ID then
    AlLlegarALaPrincipal;
  if CurPageID <> wpFinished then
    Exit;
  { La ruta del registro entero no cabe en un renglón de esta página: aquí, su carpeta («Cómo quedó» la dice entera). }
  if CodigoPaso1 = 2 then
  begin
    WizardForm.FinishedHeadingLabel.Caption := 'NO VENDAS EN ESTA CAJA';
    Texto := PorQueNoVender + #13#10#13#10 + 'El detalle quedó en C:\ProgramData\PapeleriaPos\instalador.';
  end
  else if not ProgramaBien then
  begin
    WizardForm.FinishedHeadingLabel.Caption := 'No se terminó de instalar';
    { Si el script alcanzó a decir cómo quedó la mudanza antes de fallar, eso sí cambió: no todo sigue como estaba. }
    if MudanzaQuedo = '' then
      Texto := PorQueNoSeInstalo + #13#10#13#10 + 'Lo de antes sigue como estaba. El detalle quedó en C:\ProgramData\PapeleriaPos\instalador.'
    else
      Texto := PorQueNoSeInstalo + #13#10#13#10 + 'Ojo: lo de la mudanza sí cambió (lo dice «Cómo quedó», la página de antes). El detalle quedó en '
        + 'C:\ProgramData\PapeleriaPos\instalador.';
  end
  else
  begin
    WizardForm.FinishedHeadingLabel.Caption := 'Papelería POS quedó instalada';
    { Con una mudanza, lo que sigue es lo suyo (y lo de siempre no aplica: ni «si la base es nueva» ni «el PIN del dueño»). }
    if UltimaPaginaDeLaMudanza(Titulo, Texto) then
      WizardForm.FinishedHeadingLabel.Caption := Titulo
    else if RolElegido = 'otra' then
      Texto := 'Abre la caja para terminar: se da de alta con la caja principal, con el usuario y el PIN del dueño (si no la encuentra sola, '
        + '«Buscar en la red» o su IP).'
    else
    begin
      Texto := 'Abre la caja para terminar: si la base es nueva, ahí se ponen la tienda, la cuenta del dueño y esta caja; los costos de impresión, '
        + 'en «Terminar la instalación».';
      { Con eleventa y sin tienda todavía: en el piloto la base se arma antes; si se abre la caja primero, se crea una tienda que después estorba. }
      if ElSi.Checked and (Valor(Paso2, 'tienda-nueva') = 'si') then
      begin
        Texto := 'Si vas a usar la base de eleventa (el piloto), antes de abrir la caja, arma la base: instructivo, pasos 4 y 5. Si no, ' + Lowercase(Copy(Texto, 1, 1)) + Copy(Texto, 2, Length(Texto));
        if WizardForm.RunList.Items.Count > 0 then
          WizardForm.RunList.Checked[0] := False;
      end;
      Ips := Valor(Actual, 'ips');
      if RolElegido = 'principal' then
      begin
        Texto := Texto + #13#10#13#10 + 'Las otras cajas: este mismo instalador en cada PC, eligiendo «Otra caja». Encuentran esta en la red';
        if Ips <> '' then
          Texto := Texto + ', o con su IP: ' + Ips;
        Texto := Texto + '.';
      end
      else
        Texto := Texto + #13#10#13#10 + 'Es la única caja: los celulares sí se conectan a ella; otra caja no (para eso, el instalador otra vez y '
          + '«Caja principal»).';
    end;
    { «Abrir la caja para terminar» no viene marcada si con esa mudanza no conviene abrirla todavía (se puede marcar a mano). }
    if MudanzaSinAbrirLaCaja and (WizardForm.RunList.Items.Count > 0) then
      WizardForm.RunList.Checked[0] := False;
  end;
  WizardForm.FinishedLabel.Caption := Texto;
  WizardForm.FinishedLabel.AutoSize := False;
  WizardForm.FinishedLabel.Height := ScaleY(120);
  WizardForm.RunList.Top := WizardForm.FinishedLabel.Top + WizardForm.FinishedLabel.Height + ScaleY(8);
end;

function SePuedeAbrirLaCaja: Boolean;
begin
  Result := ProgramaBien and FileExists(ExpandConstant('{app}\Caja\PapeleriaPos.Caja.exe'));
end;

function GetCustomSetupExitCode: Integer;
begin
  Result := CodigoDeSalida;
end;

{ ── Desinstalar ── }

function PreguntarAlQuitar(var Borrar: Boolean): Boolean;
var
  Forma: TSetupForm;
  Texto: TNewStaticText;
  Casilla: TNewCheckBox;
  Aceptar, Cancelar: TNewButton;
begin
  { Desde Inno Setup 6.6, el tamaño va al crearla (sin ScaleX ni ScaleY: lo escala sola) y ya no se cambia. }
  Forma := CreateCustomForm(480, 268, True, True);
  try
    Forma.Caption := 'Desinstalar Papelería POS';
    Texto := TNewStaticText.Create(Forma);
    Texto.Parent := Forma;
    Texto.AutoSize := False;
    Texto.WordWrap := True;
    Texto.Left := ScaleX(16);
    Texto.Top := ScaleY(16);
    Texto.Width := Forma.ClientWidth - ScaleX(32);
    Texto.Height := ScaleY(178);
    Texto.Caption := 'Se quitan de esta PC la caja, el servicio (si lo tiene), su acceso del escritorio y su firewall. Si la caja está abierta, '
      + 'se cierra: antes, cobra o deja en espera la venta.' + #13#10#13#10
      + 'Con el servicio se van sus variables: la clave de Firebird y el código de Control Print. Si se vuelve a instalar, hay que '
      + 'escribirlos otra vez.' + #13#10#13#10
      + 'La base de la tienda, sus respaldos, la configuración de esta PC y el alta de la caja se quedan en C:\ProgramData\PapeleriaPos, salvo '
      + 'que marques la casilla. Lo de eleventa que hay ahí (sus respaldos, su gbak y lo del ensayo) se queda siempre. eleventa y Control '
      + 'Print no se tocan.';
    Casilla := TNewCheckBox.Create(Forma);
    Casilla.Parent := Forma;
    Casilla.Left := ScaleX(16);
    Casilla.Top := ScaleY(200);
    Casilla.Width := Forma.ClientWidth - ScaleX(32);
    Casilla.Height := ScaleY(17);
    Casilla.Caption := 'Borrar también los datos (lo de eleventa se queda)';
    Casilla.Checked := Borrar;
    Aceptar := TNewButton.Create(Forma);
    Aceptar.Parent := Forma;
    Aceptar.Caption := 'Desinstalar';
    Aceptar.Width := ScaleX(100);
    Aceptar.Height := ScaleY(23);
    Aceptar.Left := Forma.ClientWidth - ScaleX(16 + 100 + 8 + 100);
    Aceptar.Top := Forma.ClientHeight - ScaleY(23 + 14);
    Aceptar.ModalResult := mrOk;
    Cancelar := TNewButton.Create(Forma);
    Cancelar.Parent := Forma;
    Cancelar.Caption := 'Cancelar';
    Cancelar.Width := ScaleX(100);
    Cancelar.Height := ScaleY(23);
    Cancelar.Left := Forma.ClientWidth - ScaleX(16 + 100);
    Cancelar.Top := Forma.ClientHeight - ScaleY(23 + 14);
    Cancelar.ModalResult := mrCancel;
    Cancelar.Cancel := True;
    Forma.ActiveControl := Cancelar;
    Result := Forma.ShowModal = mrOk;
    Borrar := Casilla.Checked;
  finally
    Forma.Free;
  end;
  if Result and Borrar then
    Result := MsgBox('¿Seguro? Se borran de esta PC la base de la tienda con sus respaldos (lo vendido, el catálogo, los usuarios), la '
      + 'configuración de esta PC y el alta de la caja, y no hay forma de regresarlos desde aquí.' + #13#10#13#10
      + 'Se quedan los respaldos de eleventa, su copia de Firebird 2.0 (el gbak) y lo del ensayo.', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES;
end;

function InitializeUninstall: Boolean;
begin
  Result := True;
  if CheckForMutexes(MutexDelInstalador) then
  begin
    if not UninstallSilent then
      MsgBox('El instalador de Papelería POS se está corriendo en esta PC: espera a que termine y vuelve a desinstalar.', mbError, MB_OK);
    Result := False;
    Exit;
  end;
  BorrarDatosAlQuitar := Lowercase(ExpandConstant('{param:BORRARDATOS|no}')) = 'si';
  if not UninstallSilent then
    Result := PreguntarAlQuitar(BorrarDatosAlQuitar);
  { A media mudanza (la marca junto a la base) no se borran los datos: la caja de esta PC puede tener ventas sin subir. instalar-pc.ps1 también
    se niega, pero cuando corre ya no se puede detener la desinstalación: se irían el desinstalador y su entrada de «Aplicaciones» con el POS
    todavía en la PC. Aquí sí se detiene, antes de quitar nada. En silencio, sin ventana: solo el registro y el código de salida. }
  if Result and BorrarDatosAlQuitar and (FileExists(ExpandConstant('{commonappdata}\PapeleriaPos\pos.db.mudada'))
    or DirExists(ExpandConstant('{commonappdata}\PapeleriaPos\pos.db.mudada'))) then
  begin
    Log('No se desinstala: esta PC está a media mudanza (la marca pos.db.mudada) y se pidió borrar también los datos.');
    if not UninstallSilent then
      MsgBox('Esta PC está a media mudanza y su caja puede tener ventas sin subir: primero corre el instalador y termina o cancela la mudanza.'
        + #13#10#13#10 + 'No se quitó nada.', mbError, MB_OK);
    Result := False;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Codigo: Integer;
  Archivo, Mas: String;
  Salida: TArrayOfString;
begin
  if CurUninstallStep <> usUninstall then
    Exit;
  Mas := '';
  if BorrarDatosAlQuitar then
  begin
    { Con los datos se va la carpeta de los registros: este queda en la carpeta temporal de esta cuenta de Windows (la del
      desinstalador se borra al cerrarlo, y el mensaje de abajo dice dónde buscarlo). }
    Registro := GetTempDir + 'papeleria-pos-desinstalar-' + GetDateTimeString('yyyy/mm/dd_hh:nn:ss', '-', '.') + '.log';
    Mas := ' -BorrarDatos';
  end
  else
    Registro := ExpandConstant('{commonappdata}\PapeleriaPos\instalador\desinstalar-') + GetDateTimeString('yyyy/mm/dd_hh:nn:ss', '-', '.') + '.log';
  Archivo := ExpandConstant('{tmp}\quitar.txt');
  if not Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'), '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "'
    + ExpandConstant('{app}\instalador\instalar-pc.ps1') + '" -Paso quitar -Registro "' + Registro + '" -Resultado "' + Archivo + '"' + Mas, '',
    SW_HIDE, ewWaitUntilTerminated, Codigo) then
    Codigo := -1;
  Log('instalar-pc.ps1 -Paso quitar terminó con ' + IntToStr(Codigo));
  if not LoadStringsFromFile(Archivo, Salida) then
    SetArrayLength(Salida, 0);
  { Con 0 y sin decir cómo quedó tampoco se sabe si se quitó. }
  if ((Codigo <> 0) or (Valor(Salida, 'quitado') <> 'si')) and not UninstallSilent then
  begin
    Mas := Valor(Salida, 'motivo');
    if Mas = '' then
      Mas := 'el paso de desinstalar terminó con ' + IntToStr(Codigo) + ' sin decir cómo quedó';
    MsgBox('No se quitó todo: ' + Mas + #13#10#13#10 + 'Lo que pasó quedó en ' + Registro + '.', mbError, MB_OK);
  end;
end;
