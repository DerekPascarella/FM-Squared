@echo off
scan.exe \ data\
mkisofs -iso-level 1 -volid Almanac -G boot_cd -sort layout -l -o ALMANAC.ISO data
fixup ALMANAC.ISO
