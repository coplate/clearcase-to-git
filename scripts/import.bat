@echo on
set batch_location=%~dp0
set source_location=%batch_location%\..\

copy %source_location%\App.config %source_location%\scripts\GitImporter.exe.config
copy %source_location%\protobuf-net.* %source_location%\scripts\
C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe  /debug /define:DEBUG /define:TRACE /r:protobuf-net.dll /out:%batch_location%\GitImporter.exe /pdb:%batch_location%\GitImporter %source_location%\*.cs 

goto err

if [%1]==[] goto usage
if [%2]==[] goto usage
if [%3]==[] goto usage
if [%4]==[] goto usage
if [%5]==[] goto usage
if [%6]==[] goto usage

set clearcase_view_root=%1
set clearcase_pvob=%2
set clearcase_project=%3
set git_workspace=%4
set search_type=%5
set use_export=%6
 
set search_flag=-all
if [%search_type%] == [vob] set search_flag=
set pvob_root=%clearcase_view_root%\%clearcase_pvob%
 
set export_dir=%git_workspace%\clearcase-to-git-export
set rel_export_dir=clearcase-to-git-export
mkdir %git_workspace%\clearcase-to-git-export
 
 
if [%use_export%] == [true] goto export
goto check_search_type
 
:export
set export_flag=%export_dir%\%clearcase_project%.export
if exist %rel_export_dir%\%clearcase_project%.export goto find_files_to_import
echo Creating Project export file, this may take a very long time
pushd %clearcase_pvob%
echo %cd%
clearexport_ccase -r -o %export_dir%\%clearcase_project%.export %clearcase_project% >%export_dir%\%clearcase_project%.export.log
popd
echo %cd%
 
:check_search_type
 
if [%search_type%] == [vob] goto find_files_to_import
if [%search_type%] == [project] goto find_files_to_import
goto usage
 
:find_files_to_import
if exist %export_dir%\%clearcase_pvob%.project_files goto filter_files
echo Creating Project file list, this may take a very long time
pushd %pvob_root%
echo %cd%
@echo finding directories...
cleartool find %clearcase_project% %search_flag=% -type d -print >%export_dir%\%clearcase_pvob%.%search_type%_dirs
@echo finding files...
cleartool find %clearcase_project% %search_flag=% -type f -print >%export_dir%\%clearcase_pvob%.%search_type%_files
popd
echo %cd%
 
 
:filter_files
if exist %rel_export_dir%\%clearcase_project%.import_files goto build_vodb
echo filterin files...
 
ccperl filter.pl D %pvob_root% %clearcase_project% %export_dir%\%clearcase_pvob%.%search_type%_dirs>%export_dir%\%clearcase_project%.import_dirs
ccperl filter.pl F %pvob_root% %clearcase_project% %export_dir%\%clearcase_pvob%.%search_type%_files >%export_dir%\%clearcase_project%.import_files
   
 
:build_vodb
 
if exist %rel_export_dir%\%clearcase_project%.vodb goto create_partial_import
 
if exist %export_dir%\build_%clearcase_project%_vodb.log move %export_dir%\build_%clearcase_project%_vodb.log %export_dir%\build_%clearcase_project%_vodb.%DATE:~-4%-%DATE:~4,2%-%DATE:~7,2%.log
if exist %export_dir%\%clearcase_project%.vodb move %export_dir%\%clearcase_project%.vodb %export_dir%\%clearcase_project%.%DATE:~-4%-%DATE:~4,2%-%DATE:~7,2%.vodb
 
%batch_location%\GitImporter.exe /S:%rel_export_dir%\%clearcase_project%.vodb /C:%pvob_root% /Branches:^^.*  /D:%rel_export_dir%\%clearcase_project%.import_dirs /E:%rel_export_dir%\%clearcase_project%.import_files /R:. /R:%clearcase_project% /P:./%clearcase_project% /P:%clearcase_project% %export_flag% /G >%export_dir%\build_%clearcase_project%_vodb.output
if %errorlevel% neq 0 goto err
if not exist %rel_export_dir%\%clearcase_project%.vodb (
    rem file doesn't exist
    goto err
)
move %batch_location%\GitImporter.log %export_dir%\build_%clearcase_project%_vodb.log
 
:create_partial_import
 
if exist %export_dir%\%clearcase_project%_history.bin move %export_dir%\%clearcase_project%_history.bin  %export_dir%\%clearcase_project%_history.%DATE:~-4%-%DATE:~4,2%-%DATE:~7,2%.bin
%batch_location%\GitImporter.exe /L:%rel_export_dir%\%clearcase_project%.vodb /C:%pvob_root% /Branches:^^.* /H:%export_dir%\%clearcase_project%_history.bin /R:. /R:%clearcase_project% /P:./%clearcase_project% /P:%clearcase_project% /N >%export_dir%\%clearcase_project%_import.partial 2>%export_dir%\%clearcase_project%_import.partial.err
if %errorlevel% neq 0 goto err
if not exist %rel_export_dir%\%clearcase_project%_history.bin (
    rem file %rel_export_dir%\%clearcase_project%_history.bin doesn't exist
    goto err
)
move %batch_location%\GitImporter.log %export_dir%\create_%clearcase_project%_import_partial.log
 
 
:create_full_import
 
%batch_location%\GitImporter.exe /C:%pvob_root% /F:%export_dir%\%clearcase_project%_import.partial /Branches:^^.*  /R:. /R:%clearcase_project% /P:./%clearcase_project% /P:%clearcase_project% > %export_dir%\%clearcase_project%_import.full
if %errorlevel% neq 0 goto err
move %batch_location%\GitImporter.log %export_dir%\create_%clearcase_project%_import_full.log
 
 
:create_repo
mkdir git
rmdir /S /Q git\%clearcase_project%.git
git init --bare git\%clearcase_project%.git
git -C git\%clearcase_project%.git config core.ignorecase false
git -C git\%clearcase_project%.git config core.autocrlf false
 
 
if exist %export_dir%\%clearcase_project%.marks move %export_dir%\%clearcase_project%.marks %export_dir%\%clearcase_project%.%DATE:~-4%-%DATE:~4,2%-%DATE:~7,2%.marks
git -C git\%clearcase_project%.git fast-import --export-marks=%export_dir%\%clearcase_project%.marks < %export_dir%\%clearcase_project%_import.full > %export_dir%\%clearcase_project%_fastimport.log
 
 
echo repacking repo...
git -C git\%clearcase_project%.git repack -a -d -f --window-memory=50m
echo > git\%clearcase_project%.git\git-daemon-export-ok
REM git add origin ssh:///.......
REM git push -u origina --all
REM git push -u origina --tags
goto :eof
 
:usage
@echo Usage: %0 ^<Drive:\view^> ^<vob^> ^<project^> ^<Drive:\git_parent_folder^> ^<file_search_type ^( vob ^| project ^)^> ^<use_export_flag ^( true ^| false^)^>
@echo    ex: %0 k:\mdcarls_shell_1.1_int af_pvob %clearcase_project% c:\ccgit vob
@echo  Note: When use_export_flag=true, the process will run faster, but will not process activity names
@echo  Note: If file_search_type=project gives you errors further down the line, you many need to do a full vob file listing in order to process files and history correctly.
exit /B 1
:err
echo ERROR
 
 
