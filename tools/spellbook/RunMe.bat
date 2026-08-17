@echo off
scan.exe \ data\
mkisofs -iso-level 1 -volid SpellBook -G boot_cd -sort layout -l -o SPLLBOOK.ISO data
fixup SPLLBOOK.ISO
