#requires -Version 5.1
[CmdletBinding()]
param([string]$RepositoryRoot = (Get-Location).Path)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath $RepositoryRoot).Path
$updates = [ordered]@{}

# Reads source and normalizes line endings.
function Read-Source([string]$RelativePath) {
    $path = Join-Path $root $RelativePath
    return [IO.File]::ReadAllText($path).Replace("`r`n", "`n")
}

# Normalizes both source and replacement strings before matching.
function Replace-Once([string]$Source, [string]$Old, [string]$New) {
    $Source = $Source.Replace("`r`n", "`n")
    $Old = $Old.Replace("`r`n", "`n")
    $New = $New.Replace("`r`n", "`n")

    if ([regex]::Matches($Source, [regex]::Escape($Old)).Count -ne 1) {
        throw "Expected exactly one source occurrence: $Old"
    }

    return $Source.Replace($Old, $New)
}

foreach ($file in @('TechMES.Infrastructure.PostgreSql/Connections/PostgreSqlModuleDatabaseSettings.cs', 'TechMES.Maintenance/MainWindow.PostgreSqlDatabases.cs', 'TechMES.Maintenance/ViewModels/TypedAppSettingsViewModel.PostgreSqlDatabases.cs')) {
    if (!(Test-Path -LiteralPath (Join-Path $root $file) -PathType Leaf)) {
        throw "Create the new source file first: $file"
    }
}

$file = 'TechMES.Maintenance/TechMES.Maintenance.csproj'
$s = Read-Source $file

$old = '<ProjectReference Include="..\TechMES.Contracts\TechMES.Contracts.csproj" />'
$new = $old + "`n" + '        <ProjectReference Include="..\TechMES.Infrastructure.PostgreSql\TechMES.Infrastructure.PostgreSql.csproj" />'

$s = Replace-Once $s $old $new
$updates[$file] = $s

$file = 'TechMES.Maintenance/ViewModels/TypedAppSettingsViewModel.cs'
$s = Read-Source $file

$s = Replace-Once $s 'public sealed class TypedAppSettingsViewModel' 'public sealed partial class TypedAppSettingsViewModel'
$s = Replace-Once $s ('    private string _runtimeEventDatabaseConnectionString = "";' + "`n") ''

# Matches the original property independently of whitespace and line endings.
$pattern = 'public\s+string\s+RuntimeEventDatabaseConnectionString\s*\{\s*get\s*=>\s*_runtimeEventDatabaseConnectionString\s*;\s*set\s*=>\s*SetProperty\(\s*ref\s+_runtimeEventDatabaseConnectionString\s*,\s*value\s*\)\s*;\s*\}'

if ([regex]::Matches($s, $pattern).Count -ne 1) {
    throw 'Expected one original RuntimeEventDatabaseConnectionString property.'
}

$s = [regex]::Replace($s, $pattern, 'public string RuntimeEventDatabaseConnectionString => ModuleDiagnosticConnectionString(RuntimeEventDatabase);')

if ($s.Contains('_runtimeEventDatabaseConnectionString')) {
    throw 'An unexpected reference to the old Event DB backing field remains.'
}

$updates[$file] = $s

$file = 'TechMES.Maintenance/MainWindow.xaml.cs'
$s = Read-Source $file

$s = Replace-Once $s '            TypedAppSettings.RuntimeEventDatabaseConnectionString = GetString(runtime, "EventDatabase", "ConnectionString");' '            LoadPostgreSqlModuleDatabases(runtime);'
$s = [regex]::Replace($s, '(?m)^\s*// Event DB[^\n]*\n', '')

$s = Replace-Once $s '        SetValue(root, TypedAppSettings.RuntimeEventDatabaseConnectionString, "EventDatabase", "ConnectionString");' '        ApplyPostgreSqlModuleDatabases(root);'

$old = '        await AddPostgresAuthCheckAsync("Event PostgreSQL", TypedAppSettings.RuntimeEventDatabaseConnectionString);'
$new = $old + "`n" + '        await AddPostgresTcpCheckAsync("Recipes PostgreSQL", TypedAppSettings.RuntimeRecipeDatabaseConnectionString);' + "`n" + '        await AddPostgresAuthCheckAsync("Recipes PostgreSQL", TypedAppSettings.RuntimeRecipeDatabaseConnectionString);'

$s = Replace-Once $s $old $new

$s = $s.Replace('Runtime EventDatabase:ConnectionString is empty.', 'Main DB connection or EventDatabase:Database is empty.')
$s = $s.Replace('Operation actions cannot be loaded without Event DB connection string.', 'Operation actions require Main DB connection settings and EventDatabase:Database.')

$updates[$file] = $s

$file = 'TechMES.Maintenance/Views/SettingsPage.xaml'
$s = Read-Source $file

$pattern = '(?s)                        <views:MaintenanceSection Header="PostgreSQL Event DB".*?</views:MaintenanceSection>\n'

if ([regex]::Matches($s, $pattern).Count -ne 1) {
    throw 'Expected one old Event DB section.'
}

$s = [regex]::Replace($s, $pattern, '')

$pattern = '(?s)(                        <views:MaintenanceSection Header="PostgreSQL Main DB".*?)(                            </StackPanel>\n                        </views:MaintenanceSection>)'
$match = [regex]::Match($s, $pattern)

if (!$match.Success) {
    throw 'Main DB section was not found.'
}

$rows = @'
                                <Grid Margin="0,0,0,10">
                                    <Grid.ColumnDefinitions>
                                        <ColumnDefinition Width="240" />
                                        <ColumnDefinition Width="Auto" />
                                    </Grid.ColumnDefinitions>
                                    <TextBlock Text="Recipes database" Style="{StaticResource SettingsFieldLabel}" />
                                    <StackPanel Grid.Column="1">
                                        <ui:TextBox Text="{Binding TypedAppSettings.RuntimeRecipesDatabase, UpdateSourceTrigger=PropertyChanged}" Width="682" Style="{StaticResource SettingsTextBox}" />
                                        <TextBlock Text="Database name, for example RecipeKNG. Uses Main DB connection settings." Style="{StaticResource SettingsHintText}" />
                                    </StackPanel>
                                </Grid>
                                <Grid Margin="0,0,0,10">
                                    <Grid.ColumnDefinitions>
                                        <ColumnDefinition Width="240" />
                                        <ColumnDefinition Width="Auto" />
                                    </Grid.ColumnDefinitions>
                                    <TextBlock Text="EventPicker database" Style="{StaticResource SettingsFieldLabel}" />
                                    <StackPanel Grid.Column="1">
                                        <ui:TextBox Text="{Binding TypedAppSettings.RuntimeEventDatabase, UpdateSourceTrigger=PropertyChanged}" Width="682" Style="{StaticResource SettingsTextBox}" />
                                        <TextBlock Text="Database name, for example EventPicker. Uses the Main DB Primary/Secondary pair." Style="{StaticResource SettingsHintText}" />
                                    </StackPanel>
                                </Grid>
'@

$rows = $rows.Replace("`r`n", "`n")

$s = $s.Substring(0, $match.Index) + $match.Groups[1].Value + $rows + "`n" + $match.Groups[2].Value + $s.Substring($match.Index + $match.Length)

$updates[$file] = $s

# Validates all replacements before writing and backs up every replaced file.
$backupRoot = Join-Path $root ('artifacts/module-database-ui-backup-' + [guid]::NewGuid().ToString('N'))

foreach ($entry in $updates.GetEnumerator()) {
    $path = Join-Path $root $entry.Key
    $backup = Join-Path $backupRoot $entry.Key

    $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($backup))

    $temporary = $path + '.' + [guid]::NewGuid().ToString('N') + '.tmp'

    [IO.File]::WriteAllText($temporary, $entry.Value, (New-Object Text.UTF8Encoding($true)))
    [IO.File]::Replace($temporary, $path, $backup)

    Write-Host ('UPDATED: ' + $entry.Key)
}

Write-Host ('BACKUP: ' + $backupRoot)