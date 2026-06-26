$f = "Views/SidebarView.xaml"
$c = [System.IO.File]::ReadAllText((Resolve-Path $f))

# Remove old duplicate ProjectsHost block (Padding="8,8" before the Padding="8,8,8,8" line)
$old1 = @"
                <ScrollViewer VerticalScrollBarVisibility="Auto" Padding="8,8"
                              Style="{StaticResource OverlayVScroll}">
                    <ItemsControl x:Name="ProjectsHost" Tag="1"
                <ScrollViewer VerticalScrollBarVisibility="Auto" Padding="8,8,8,8"
"@

$new1 = @"
                <ScrollViewer VerticalScrollBarVisibility="Auto" Padding="8,8,8,8"
"@

$c = $c.Replace($old1, $new1)

# Remove old duplicate ArchivedHost block
$old2 = @"
                <ScrollViewer VerticalScrollBarVisibility="Auto" Padding="8,8"
                              Style="{StaticResource OverlayVScroll}">
                    <ItemsControl x:Name="ArchivedHost" Tag="1"
                <ScrollViewer VerticalScrollBarVisibility="Auto" Padding="8,8,8,8"
"@

$c = $c.Replace($old2, $new1.Replace("ProjectsHost", "ArchivedHost"))

[System.IO.File]::WriteAllText((Resolve-Path $f), $c, [System.Text.UTF8Encoding]::new($true))
Write-Host "Done - cleaned up duplicates"
