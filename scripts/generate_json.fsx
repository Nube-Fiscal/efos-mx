// Descarga el listado 69-B del SAT, lo convierte a JSON estructurado
// y genera el diff diario contra la version anterior.
//
// Salida en docs/:
//   metadata.json         - liviano, para polling eficiente
//   listado.json          - listado completo
//   diff/YYYY-MM-DD.json  - que cambio hoy
//
// Uso: dotnet fsi scripts/generate_json.fsx

open System
open System.Collections.Generic
open System.IO
open System.Net.Http
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Serialization

// ---------------------------------------------------------------------------
// Config
// ---------------------------------------------------------------------------

let [<Literal>] SatUrl =
    "https://wu1agsprosta001.blob.core.windows.net\
/agsc-publicaciones/Datos_abiertos/Documents_AGAFF\
/Listado_completo_69-B.csv"

let docsPath = "docs"
let diffPath = Path.Combine(docsPath, "diff")
Directory.CreateDirectory(docsPath) |> ignore
Directory.CreateDirectory(diffPath) |> ignore

let jsonOpts =
    let o = JsonSerializerOptions(WriteIndented = true)
    o.PropertyNamingPolicy <- JsonNamingPolicy.SnakeCaseLower
    o.DefaultIgnoreCondition <- JsonIgnoreCondition.WhenWritingNull
    o

// ---------------------------------------------------------------------------
// Tipos internos del script
// ---------------------------------------------------------------------------

[<CLIMutable>]
type Entry = {
    Rfc: string
    Nombre: string
    Situacion: string
    DofPresuncion: string
    SatPresuncion: string
    DofDefinitivo: string
    SatDefinitivo: string
    DofDesvirtuacion: string
    SatDesvirtuacion: string
    DofSentencia: string
    SatSentencia: string
}

[<CLIMutable>]
type Metadata = {
    UltimaActualizacion: string
    TotalRegistros: int
    Sha256: string
    Fuente: string
    DiffDisponible: string
}

[<CLIMutable>]
type Listado = {
    Metadata: Metadata
    Data: Entry[]
}

type DiffResumen = {
    TotalAnterior: int
    TotalActual: int
    Agregados: int
    Eliminados: int
}

type Diff = {
    Fecha: string
    Resumen: DiffResumen
    Agregados: Entry[]
    Eliminados: Entry[]
}

// ---------------------------------------------------------------------------
// Descarga
// ---------------------------------------------------------------------------

let downloadCsv () =
    printfn "Descargando: %s" SatUrl
    use client = new HttpClient()
    client.DefaultRequestHeaders.Add("User-Agent", "efos-mx/1.0")
    client.Timeout <- TimeSpan.FromSeconds(60.0)
    let bytes = client.GetByteArrayAsync(SatUrl).Result
    printfn "  %d bytes descargados" bytes.Length
    bytes

let decodeCsv (raw: byte[]) =
    // Decoders lazys: cada factory se evalua solo si el anterior fallo.
    // UTF-8 estricto: si el archivo es Latin-1, bytes invalidos lanzan excepcion
    // y caemos a iso-8859-1. El GetEncoding("utf-8") por defecto NO lanza -
    // sustituye con U+FFFD corrompiendo acentos silenciosamente.
    let candidates : (unit -> Encoding) list = [
        fun () -> new UTF8Encoding(encoderShouldEmitUTF8Identifier = true,  throwOnInvalidBytes = true)
        fun () -> new UTF8Encoding(encoderShouldEmitUTF8Identifier = false, throwOnInvalidBytes = true)
        fun () -> Encoding.GetEncoding("iso-8859-1")
    ]
    candidates
    |> List.tryPick (fun mkEnc ->
        try Some (mkEnc().GetString(raw))
        with _ -> None)
    |> Option.defaultWith (fun () -> failwith "No se pudo decodificar el CSV")

// ---------------------------------------------------------------------------
// Parseo CSV
// ---------------------------------------------------------------------------

// Mapeo flexible: nombre columna SAT normalizado -> clave interna
// Columnas reales del archivo 69-B (verificadas contra el CSV del SAT):
//   No, RFC, Nombre del Contribuyente, Situacion del contribuyente,
//   Numero y fecha de oficio global de presuncion SAT,
//   Publicacion pagina SAT presuntos, Numero y fecha... DOF,
//   Publicacion DOF presuntos, ...desvirtuados..., ...definitivos...,
//   ...sentencia favorable...
let columnAliases =
    dict [
        // Identificacion
        "rfc",                                          "rfc"
        "nombre del contribuyente",                     "nombre"
        "razon social",                                 "nombre"
        "razon social o denominacion",                  "nombre"
        "nombre",                                       "nombre"
        "situacion del contribuyente",                  "situacion"
        "situacion",                                    "situacion"
        // Publicaciones presuntos (nombres reales en el CSV del SAT)
        "publicacion pagina sat presuntos",             "sat_presuncion"
        "publicacion dof presuntos",                    "dof_presuncion"
        // Publicaciones desvirtuados
        "publicacion pagina sat desvirtuados",          "sat_desvirtuacion"
        "publicacion dof desvirtuados",                 "dof_desvirtuacion"
        // Publicaciones definitivos
        "publicacion pagina sat definitivos",           "sat_definitivo"
        "publicacion dof definitivos",                  "dof_definitivo"
        // Publicaciones sentencia favorable
        "publicacion pagina sat sentencia favorable",   "sat_sentencia"
        "publicacion dof sentencia favorable",          "dof_sentencia"
        // Aliases alternativos por si el SAT cambia nombres en el futuro
        "publicacion en el dof de la presuncion",       "dof_presuncion"
        "publicacion en la pagina del sat de la presuncion", "sat_presuncion"
        "publicacion en el dof definitivo",             "dof_definitivo"
        "publicacion en la pagina del sat definitivo",  "sat_definitivo"
        "publicacion en el dof de desvirtuacion",       "dof_desvirtuacion"
        "publicacion en el dof de sentencia favorable", "dof_sentencia"
    ]

let normalizeHeader (h: string) =
    // NFD descompone letras acentuadas en letra base + diacritico combinante.
    // Luego filtramos los NonSpacingMark (diacriticos) y quedamos con ASCII limpio.
    let nfd = h.Trim().Trim('"').ToLowerInvariant().Normalize(System.Text.NormalizationForm.FormD)
    nfd
    |> Seq.filter (fun c ->
        Globalization.CharUnicodeInfo.GetUnicodeCategory(c) <>
        Globalization.UnicodeCategory.NonSpacingMark)
    |> System.String.Concat

// Parseo de CSV: el CSV del SAT usa comas y puede tener celdas entre comillas
let splitCsvLine (line: string) =
    // Separar respetando comillas (campos que contienen comas)
    let mutable fields = ResizeArray<string>()
    let mutable inQuote = false
    let mutable current = System.Text.StringBuilder()
    for c in line do
        match c, inQuote with
        | '"', _     -> inQuote <- not inQuote
        | ',', false -> fields.Add(current.ToString().Trim()); current.Clear() |> ignore
        | _          -> current.Append(c) |> ignore
    fields.Add(current.ToString().Trim())
    fields.ToArray()

// El CSV del SAT tiene lineas de preambulo antes del header real.
// Buscamos la primera linea que tenga "RFC" entre sus primeras columnas.
let findHeaderLine (lines: string[]) =
    lines
    |> Array.tryFindIndex (fun line ->
        let fields = splitCsvLine line
        fields |> Array.exists (fun f -> f.Trim().ToUpperInvariant() = "RFC"))
    |> Option.defaultWith (fun () -> failwith "No se encontro la fila de encabezados (RFC) en el CSV")

let parseRows (text: string) : Entry[] =
    let lines =
        text.Split([| "\r\n"; "\n"; "\r" |], StringSplitOptions.RemoveEmptyEntries)

    if lines.Length < 2 then failwith "CSV vacio o sin datos"

    let headerIdx = findHeaderLine lines
    let headers   = splitCsvLine lines[headerIdx]

    // Construir mapa indice -> clave interna
    let colMap =
        headers
        |> Array.mapi (fun i h ->
            let key = normalizeHeader h
            match columnAliases.TryGetValue(key) with
            | true, mapped -> Some (i, mapped)
            | _ -> None)
        |> Array.choose id
        |> dict

    let get (row: string[]) key =
        colMap
        |> Seq.tryFind (fun kv -> kv.Value = key)
        |> Option.map (fun kv -> if kv.Key < row.Length then row[kv.Key] else "")
        |> Option.defaultValue ""

    lines[(headerIdx + 1)..]
    |> Array.choose (fun line ->
        if String.IsNullOrWhiteSpace(line) then None
        else
            let row = splitCsvLine line
            let rfc = (get row "rfc").ToUpperInvariant()
            if String.IsNullOrEmpty(rfc) then None
            else
                Some {
                    Rfc             = rfc
                    Nombre          = get row "nombre"
                    Situacion       = get row "situacion"
                    DofPresuncion   = get row "dof_presuncion"
                    SatPresuncion   = get row "sat_presuncion"
                    DofDefinitivo   = get row "dof_definitivo"
                    SatDefinitivo   = get row "sat_definitivo"
                    DofDesvirtuacion = get row "dof_desvirtuacion"
                    SatDesvirtuacion = get row "sat_desvirtuacion"
                    DofSentencia    = get row "dof_sentencia"
                    SatSentencia    = get row "sat_sentencia"
                })

// ---------------------------------------------------------------------------
// Hash y diff
// ---------------------------------------------------------------------------

let sha256Of (entries: Entry[]) =
    let json = JsonSerializer.Serialize(entries, jsonOpts)
    let bytes = Encoding.UTF8.GetBytes(json)
    SHA256.HashData(bytes) |> Convert.ToHexStringLower

let computeDiff (prev: Entry[]) (curr: Entry[]) =
    let prevByRfc = prev |> Array.map (fun e -> e.Rfc, e) |> dict
    let currByRfc = curr |> Array.map (fun e -> e.Rfc, e) |> dict

    let agregados  = curr |> Array.filter (fun e -> not (prevByRfc.ContainsKey(e.Rfc)))
    let eliminados = prev |> Array.filter (fun e -> not (currByRfc.ContainsKey(e.Rfc)))

    {
        Fecha    = DateTime.UtcNow.ToString("yyyy-MM-dd")
        Resumen  = {
            TotalAnterior = prev.Length
            TotalActual   = curr.Length
            Agregados     = agregados.Length
            Eliminados    = eliminados.Length
        }
        Agregados  = agregados
        Eliminados = eliminados
    }

// ---------------------------------------------------------------------------
// I/O
// ---------------------------------------------------------------------------

let loadPrevious () =
    let path = Path.Combine(docsPath, "listado.json")
    if not (File.Exists(path)) then [||]
    else
        let json = File.ReadAllText(path, Encoding.UTF8)
        let listado = JsonSerializer.Deserialize<Listado>(json, jsonOpts)
        if isNull (box listado) then [||] else listado.Data

let writeJson (path: string) (value: 'a) =
    let json = JsonSerializer.Serialize(value, jsonOpts)
    File.WriteAllText(path, json, Encoding.UTF8)
    printfn "  Escrito: %s (%d bytes)" path (FileInfo(path).Length)

let setOutput (name: string) (value: string) =
    match Environment.GetEnvironmentVariable("GITHUB_OUTPUT") with
    | null | "" -> printfn "[output] %s=%s" name value
    | ghOutput  -> File.AppendAllText(ghOutput, $"{name}={value}\n")

// ---------------------------------------------------------------------------
// Main
// ---------------------------------------------------------------------------

let run () =
    let raw     = downloadCsv ()
    let text    = decodeCsv raw
    let entries = parseRows text

    if entries.Length = 0 then
        eprintfn "ERROR: no se parseo ninguna entrada"
        Environment.Exit(1)

    printfn "  %d contribuyentes parseados" entries.Length

    let currentHash = sha256Of entries
    let prev        = loadPrevious ()
    let prevHash    = if prev.Length > 0 then sha256Of prev else ""

    let force   = (Environment.GetEnvironmentVariable("FORCE") |> Option.ofObj |> Option.defaultValue "") = "true"
    let changed = force || currentHash <> prevHash

    if not changed then
        printfn "Sin cambios respecto al listado anterior."
        setOutput "changed" "false"
    else
        let now    = DateTime.UtcNow.ToString("o")
        let today  = DateTime.UtcNow.ToString("yyyy-MM-dd")

        let meta = {
            UltimaActualizacion = now
            TotalRegistros      = entries.Length
            Sha256              = currentHash
            Fuente              = SatUrl
            DiffDisponible      = $"diff/{today}.json"
        }

        let diff = computeDiff prev entries

        printfn "Escribiendo archivos..."
        writeJson (Path.Combine(docsPath, "metadata.json")) meta
        writeJson (Path.Combine(docsPath, "listado.json"))  {| metadata = meta; data = entries |}
        writeJson (Path.Combine(diffPath, $"{today}.json")) diff

        printfn "  Diff: +%d agregados, -%d eliminados"
            diff.Resumen.Agregados diff.Resumen.Eliminados

        setOutput "changed" "true"

run ()
