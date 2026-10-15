@{
    RootModule        = 'RevBot.psm1'
    ModuleVersion     = '0.1.0'
    GUID              = '7c1f3a4e-2b6d-4c0e-9a8f-5d2e1b3c4a60'
    Author            = 'Revenant Systems LLC'
    CompanyName       = 'Revenant Systems LLC'
    Copyright         = '(c) Revenant Systems LLC'
    Description       = 'RevBot, the Revenant desk pet, in PowerShell: see him, ask him, chat with him, and a prompt line that shows what he is doing.'
    PowerShellVersion = '5.1'
    FunctionsToExport = @('Get-RevBotHome', 'Get-RevBotState', 'Send-RevBot', 'Start-RevBot', 'Show-RevBot', 'Invoke-RevBotAsk', 'Enter-RevBotChat', 'Get-RevBotPromptLine', 'Enable-RevBotPrompt', 'Disable-RevBotPrompt', 'Invoke-RevBot')
    CmdletsToExport   = @()
    VariablesToExport = @()
    AliasesToExport   = @('revbot')
}
