$t = Measure-Command { opencode session list --format json 2>$null | Out-Null }
"{0:N3}s" -f $t.TotalSeconds
