POWERSHELL ?= powershell.exe
.PHONY: help build status backup restart restart-force install repair-night-cpu restore
help:
	@echo build status backup restart restart-force install repair-night-cpu
	@echo restore BACKUP=path-to-local-maintenance-backup
build:
	$(POWERSHELL) -NoProfile -ExecutionPolicy Bypass -File scripts/build.ps1
status:
	$(POWERSHELL) -NoProfile -ExecutionPolicy Bypass -File scripts/maintenance.ps1 -Action Status
backup:
	$(POWERSHELL) -NoProfile -ExecutionPolicy Bypass -File scripts/maintenance.ps1 -Action Backup
restart:
	$(POWERSHELL) -NoProfile -ExecutionPolicy Bypass -File scripts/maintenance.ps1 -Action Restart
restart-force:
	$(POWERSHELL) -NoProfile -ExecutionPolicy Bypass -File scripts/maintenance.ps1 -Action Restart -Force
install:
	$(POWERSHELL) -NoProfile -ExecutionPolicy Bypass -File scripts/maintenance.ps1 -Action Install
repair-night-cpu:
	$(POWERSHELL) -NoProfile -ExecutionPolicy Bypass -File scripts/maintenance.ps1 -Action RepairNightCpu
restore:
	$(POWERSHELL) -NoProfile -ExecutionPolicy Bypass -File scripts/maintenance.ps1 -Action Restore -BackupPath "$(BACKUP)"
