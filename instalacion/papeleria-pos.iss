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
; - Sin preguntar (el CI): /VERYSILENT /SUPPRESSMSGBOXES y las respuestas como /ROL=principal… (docs/instalacion.md, «Sin preguntar»).
;   Cómo quedó: el código de salida y C:\ProgramData\PapeleriaPos\instalador\resultado.txt.
; - Desinstalar quita el programa, el servicio, el firewall y el acceso; la base, los respaldos y la configuración se quedan salvo
;   que se marque «Borrar también los datos». Nunca toca eleventa ni Control Print.

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

  PaginaRol, PaginaPrincipal, PaginaImpresoras, PaginaLector, PaginaControlPrint, PaginaEleventa, PaginaEleventa2, PaginaResultado: TWizardPage;
  RolPrincipal, RolOtra, RolUnica, LectorTeclado, LectorCom, CpNo, CpSi, ElNo, ElSi: TNewRadioButton;
  NotaRol, EstadoPrincipal, EtiquetaTicketIp, AvisoCola, NotaLector, NotaCodigo, NotaEleventa, NotaGbak, NotaClave: TNewStaticText;
  EditPrincipal, EditTicketIp, EditCpServidor, EditElServidor, EditElBase, EditElCaja, EditElCajero, EditRespaldos: TNewEdit;
  EditCodigo, EditClave: TPasswordEdit;
  ComboTicket, ComboCola, ComboPuerto, ComboBaudios, ComboGbak: TNewComboBox;
  BotonBuscar, BotonProbar, BotonGbak, BotonRespaldos: TNewButton;
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

function RolElegido: String;
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

function ConServicio: Boolean;
begin
  Result := (RolElegido = 'principal') or (RolElegido = 'unica');
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
  EditElServidor.Enabled := ElSi.Checked;
  EditElBase.Enabled := ElSi.Checked;
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

  { Dónde está la caja principal (solo «Otra caja»). }
  PaginaPrincipal := CreateCustomPage(PaginaRol.ID, 'Dónde está la caja principal', 'Esta caja se conecta a la caja principal de la tienda.');
  Ancho := PaginaPrincipal.SurfaceWidth;
  Etiqueta(PaginaPrincipal, 'Su IP (por ejemplo, 192.168.1.10). Si no la sabes, búscala en la red; o déjala vacía y la caja la busca al abrirla.', 0, 0, Ancho, ScaleY(30));
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
  NotaEleventa := Etiqueta(PaginaEleventa, '', 0, ScaleY(150), Ancho, ScaleY(80));

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
  RolPrincipal.Enabled := Pos('principal', Roles) > 0;
  RolUnica.Enabled := Pos('unica', Roles) > 0;
  RolOtra.Enabled := Pos('otra', Roles) > 0;
  Texto := Valor(Actual, 'roles-motivo');
  if HayPos then
  begin
    if Texto <> '' then
      Texto := Texto + #13#10;
    Texto := Texto + 'Ya viene elegido lo que es hoy esta PC. Si la cambias de «Caja principal» a «La única caja» (o al revés), solo cambian '
      + 'su firewall y servicio.json.';
  end;
  NotaRol.Caption := Texto;

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
  CpSi.Checked := Valor(Actual, 'controlprint') = 'si';
  CpNo.Checked := not CpSi.Checked;
  EditCpServidor.Text := Valor(Actual, 'controlprint-servidor');
  NotaCodigo.Caption := 'El código va solo en el servicio de esta PC (su variable), nunca en un archivo. La caja nunca le escribe a Control Print: '
    + 'lo que se cobre aquí sigue «por cobrar» allá.';
  if HabiaCodigo then
    NotaCodigo.Caption := 'El servicio ya tiene un código: déjalo vacío para conservarlo. ' + NotaCodigo.Caption;

  HabiaClave := TieneVariable('Eleventa__Clave');
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
    { El puente ya está prendido: se apaga con el instructivo (revisando antes lo pendiente), no desde aquí. }
    ElNo.Enabled := False;
    NotaEleventa.Caption := 'Esta caja ya lee eleventa';
    if Valor(Actual, 'eleventa-escribe') = 'si' then
      NotaEleventa.Caption := NotaEleventa.Caption + ' y le escribe cada venta (eso se queda como está)';
    NotaEleventa.Caption := NotaEleventa.Caption + '. Para apagar el puente: el instructivo, «Regresar o terminar el piloto», revisando antes lo '
      + 'pendiente. La caja no le escribe a eleventa hasta que el dueño lo empiece con el instructivo.';
  end
  else if Valor(Actual, 'eleventa-servidor-del-ini') = 'si' then
    NotaEleventa.Caption := 'La IP es la que tiene el eleventa de esta PC (su pdventa.ini). ' + NotaEleventa.Caption;

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

{ Sin preguntar: lo que se dé como /ROL=… manda sobre lo que ya tiene la PC (docs/instalacion.md, «Sin preguntar»). }
procedure AplicarParametros;
var
  Texto: String;
begin
  Texto := Lowercase(Parametro('ROL'));
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

function RevisarRol: Boolean;
begin
  Result := True;
  if RolElegido = '' then
    Result := Rechazar('Elige qué es esta PC (sin preguntar: /ROL=principal, /ROL=otra o /ROL=unica).')
  else if Pos(RolElegido, Roles) = 0 then
    Result := Rechazar('Esta PC no puede ser «' + NombreDelRol(RolElegido) + '»: ' + Valor(Actual, 'roles-motivo'))
  else if ConServicio and not HayPos then
    Result := SinOtraPrincipal;
end;

function RevisarPrincipal: Boolean;
var
  Texto, Respuesta, Aviso: String;
begin
  Result := True;
  Texto := Trim(EditPrincipal.Text);
  if Texto = '' then
    Exit;
  if not EsDireccion(Texto) then
    Result := Rechazar('La caja principal va con su IP (por ejemplo, 192.168.1.10; con su puerto, de 1 a 65535, si no es el 5080), o vacía '
      + 'para que la caja la busque al abrirla.')
  else if ProbarPrincipal(Texto, Respuesta, Aviso) then
  begin
    EstadoPrincipal.Caption := 'Contesta: ' + Respuesta;
    if Aviso <> '' then
    begin
      EstadoPrincipal.Caption := EstadoPrincipal.Caption + #13#10 + 'Ojo: ' + Aviso;
      Result := Preguntar('La caja principal en ' + Texto + ' contesta, pero: ' + Aviso + #13#10#13#10
        + '¿Instalar esta caja de todos modos? Se da de alta cuando eso se arregle.', IDYES);
    end;
  end
  else
  begin
    EstadoPrincipal.Caption := 'No contestó: ' + Respuesta;
    if Copy(Respuesta, Length(Respuesta), 1) = '.' then
      Respuesta := Copy(Respuesta, 1, Length(Respuesta) - 1);
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
begin
  Result := True;
  if not ElSi.Checked then
    Exit;
  if Pos('onedrive', Lowercase(EditRespaldos.Text)) > 0 then
    Result := Rechazar('Los respaldos de eleventa nunca van en OneDrive (son datos de la tienda): elige una carpeta de esta PC.')
  else if (Trim(ComboGbak.Text) <> '') and not FileExists(Trim(ComboGbak.Text)) then
    Result := Preguntar('No está ese gbak.exe en esta PC. ¿Seguir así? (Sin él no hay respaldo propio de eleventa.)', IDYES)
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
  if Lowercase(Parametro('CERRARCAJA')) = 'si' then
    Agregar(Lineas, 'cerrar-caja', 'si')
  else
    Agregar(Lineas, 'cerrar-caja', 'no');
  SaveStringsToUTF8File(RespuestasArchivo, Lineas, False);
end;

{ Antes de cambiar nada en la PC, el ayudante revisa las respuestas como las va a usar (la orden «revisar», que no escribe nada). Falso si
  alguna no se puede usar (Texto dice cuál y por qué; Clave, su página) o si el ayudante no corrió. }
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
end;

{ La página donde se contesta esa respuesta (la clave de respuestas.txt); wpReady si no es de una página (un archivo de la PC). }
function PaginaDeLaClave(const Clave: String): Integer;
begin
  if Clave = 'rol' then
    Result := PaginaRol.ID
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
    or (Valor(Paso1, 'cola') = 'no-se-pudo') or (ClaveDice = 'no-se-pudo') or (CodigoDice = 'no-se-pudo') then
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

  Texto := 'Esta PC: ' + NombreDelRol(RolElegido) + '.' + #13#10;
  if CodigoPaso1 = 2 then
    Texto := Texto + NoVender + #13#10 + 'Qué pasó: ' + PorQueNoSeInstalo + #13#10
  else if not ProgramaBien then
    Texto := Texto + 'NO SE TERMINÓ DE INSTALAR: ' + PorQueNoSeInstalo + #13#10
      + 'Lo de antes sigue como estaba (si se estaba actualizando, regresó a la versión de antes con su respaldo).' + #13#10
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
      Texto := Texto + TextoDe('El servicio', Valor(Paso2, 'servicio'), Valor(Paso2, 'servicio-mensaje'));
      if ElSi.Checked then
      begin
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
  Mas: String;
begin
  ClaveDice := 'no-aplica';
  CodigoDice := 'no-aplica';
  WizardForm.ProgressGauge.Style := npbstMarquee;
  Registro := ExpandConstant('{commonappdata}\PapeleriaPos\instalador\instalador-') + GetDateTimeString('yyyy/mm/dd_hh:nn:ss', '-', '.') + '.log';
  ForceDirectories(ExpandConstant('{commonappdata}\PapeleriaPos\instalador'));
  RevisarWebView2;
  GuardarRespuestas;
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
  if PageID = PaginaPrincipal.ID then
    Result := RolElegido <> 'otra'
  else if (PageID = PaginaControlPrint.ID) or (PageID = PaginaEleventa.ID) then
    Result := not ConServicio
  else if PageID = PaginaEleventa2.ID then
    Result := not ConServicio or not ElSi.Checked;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  Clave, Texto: String;
  Destino: Integer;
begin
  Result := True;
  if CurPageID = PaginaRol.ID then
    Result := RevisarRol
  else if CurPageID = PaginaPrincipal.ID then
    Result := RevisarPrincipal
  else if CurPageID = PaginaImpresoras.ID then
    Result := RevisarImpresoras
  else if CurPageID = PaginaLector.ID then
    Result := RevisarLector
  else if CurPageID = PaginaControlPrint.ID then
    Result := RevisarControlPrint
  else if CurPageID = PaginaEleventa.ID then
    Result := RevisarEleventa
  else if CurPageID = PaginaEleventa2.ID then
    Result := RevisarEleventa2
  else if (CurPageID = wpReady) and not WizardSilent then
  begin
    { La caja se cierra antes de tocar nada: así no se pierde la venta que se atiende ni pisa su configuración al cerrarse. }
    while CajaAbierta do
      if MsgBox('La caja está abierta en esta PC. Cobra o deja en espera la venta que se esté atendiendo, sal con tu usuario y ciérrala '
        + '(los tickets en espera se guardan).' + #13#10#13#10 + 'Después oprime Reintentar.', mbInformation, MB_RETRYCANCEL) <> IDRETRY then
      begin
        Result := False;
        Exit;
      end;
    { Antes de cambiar nada: si una respuesta no se puede usar, se dice y se regresa a su página. }
    if not RevisarConElAyudante(Clave, Texto) then
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

function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo, MemoTypeInfo, MemoComponentsInfo, MemoGroupInfo, MemoTasksInfo: String): String;
var
  Texto, Instalada: String;
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
  nada (sale con 7) y lo dice en resultado.txt. }
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Clave, Texto: String;
begin
  Result := '';
  if not WizardSilent then
    Exit;
  if FalloSinPreguntar <> '' then
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
  Texto, Ips: String;
begin
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
    Texto := PorQueNoSeInstalo + #13#10#13#10 + 'Lo de antes sigue como estaba. El detalle quedó en C:\ProgramData\PapeleriaPos\instalador.';
  end
  else
  begin
    WizardForm.FinishedHeadingLabel.Caption := 'Papelería POS quedó instalada';
    if RolElegido = 'otra' then
      Texto := 'Abre la caja para terminar: se da de alta con la caja principal, con el usuario y el PIN del dueño (si no la encuentra sola, '
        + '«Buscar en la red» o su IP).'
    else
    begin
      Texto := 'Abre la caja para terminar: si la base es nueva, ahí se ponen la tienda, la cuenta del dueño y esta caja; los costos de impresión, '
        + 'en «Terminar la instalación».';
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
