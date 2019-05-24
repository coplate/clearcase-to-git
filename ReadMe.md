# Clearcase to Git

A tool to extract relevant meta-data from clearcase, (and save this representation), then build change set, and output this to git-fast-import.

There are samples of actual use in the `scripts` directory

## General principle :
- export as much as possible using `clearexport_ccase` (in several parts due to memory constraints of `clearexport_ccase`)
- get all elements (files and directories)
- optionally edit these lists to exclude uninteresting ones
- use `GitImporter` (which calls `cleartool`) to create (and save) a representation of the Vob
- import with `GitImporter` and `git fast-import`, `cleartool` is then used only to get the content of files

See lanfeust' origianal readme for example of individual steps

## Example of how to run this with import.bat in the scripts directory
```
git clone https://github.com/coplate/clearcase-to-git c:\clearcase-to-git
REM Edit c:\clearcase-to-git\scripts\filter.pl and set @dir_patterns and @file_patterns for your applicatoin
cd c:\clearcase-to-git
scripts\import.bat k:\view_name vob_name project_folder_name c:\clearcase-to-git
```
- Important!: your command prompt must not be in the scripts directory when you run the command, or there will be some unwanted behavior.  I got this applicaiton to work for my projects, and didnot try to address that.
- This will perform the following action as a default behavior.  
- It runs the GitImport steps in multiple phases, so you can review the output manually if you need to restart it.

```
set batch_location=c:\clearcase-to-git\scripts
set source_location=c:\clearcase-to-git\
pushd %batch_location%
 copy %source_location%\cleartool_tty.exe %source_location%\scripts\cleartool_tty.exe
 copy %source_location%\App.config %source_location%\scripts\GitImporter.exe.config
 copy %source_location%\protobuf-net.* %source_location%\scripts\
 C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe  /debug /define:DEBUG /define:TRACE /r:protobuf-net.dll /out:%batch_location%\GitImporter.exe /pdb:%batch_location%\GitImporter %source_location%\*.cs 
popd
set clearcase_view_root=k:\view_name
set clearcase_pvob=vob_name
set clearcase_project=project_folder_name
set git_workspace=c:\clearcase-to-git
set search_type=project
set use_export=false
set preserve_whitespace=false
set pvob_root=%clearcase_view_root%\%clearcase_pvob%
set export_dir=%git_workspace%\clearcase-to-git-export
mkdir %git_workspace%\clearcase-to-git-export

pushd %pvob_root%
cleartool find %clearcase_project% %search_flag% -type d -print >%export_dir%\%search_file_prefix%.%search_type%_dirs
cleartool find %clearcase_project% %search_flag% -type f -print >%export_dir%\%search_file_prefix%.%search_type%_files
popd

ccperl %batch_location%\filter.pl D %pvob_root% %clearcase_project% %export_dir%\%search_file_prefix%.%search_type%_dirs>%export_dir%\%clearcase_project%.import_dirs
ccperl %batch_location%\filter.pl F %pvob_root% %clearcase_project% %export_dir%\%search_file_prefix%.%search_type%_files >%export_dir%\%clearcase_project%.import_files

%batch_location%\GitImporter.exe /S:%export_dir%\%clearcase_project%.vodb /C:%pvob_root% /Branches:^^.*  /D:%export_dir%\%clearcase_project%.import_dirs /E:%export_dir%\%clearcase_project%.import_files /R:. /R:%clearcase_project% /P:./%clearcase_project% /P:%clearcase_project% %export_flag% /G >%export_dir%\build_%clearcase_project%_vodb.output
%batch_location%\GitImporter.exe /L:%export_dir%\%clearcase_project%.vodb /C:%pvob_root% /Branches:^^.* /H:%export_dir%\%clearcase_project%_history.bin /R:. /R:%clearcase_project% /P:./%clearcase_project% /P:%clearcase_project% /N >%export_dir%\%clearcase_project%_import.partial 2>%export_dir%\%clearcase_project%_import.partial.err
%batch_location%\GitImporter.exe /C:%pvob_root% /F:%export_dir%\%clearcase_project%_import.partial /Branches:^^.*  /R:. /R:%clearcase_project% /P:./%clearcase_project% /P:%clearcase_project% > %export_dir%\%clearcase_project%_import.full

set git_storage_dir=%git_workspace%\clearcase-to-git-export\git
set bare_repo_dir=%git_storage_dir%\%clearcase_project%.git
mkdir %git_storage_dir%
rmdir /S /Q %bare_repo_dir%
git init --bare %bare_repo_dir%
git -C %bare_repo_dir% config core.ignorecase false
git -C %bare_repo_dir% fast-import --export-marks=%export_dir%\%clearcase_project%.marks < %export_dir%\%clearcase_project%_import.full > %export_dir%\%clearcase_project%_fastimport.log
git -C %bare_repo_dir% repack -a -d -f --window-memory=50m
echo > %bare_repo_dir%\git-daemon-export-ok

```

## Thirdparties
There is support for thirdparties to be handled as git submodules, using a specific configuration file.

It assumes that there is a special file that stores the clearcase config-spec, with label rules
for some directories. Then for each new version of this file, if a match is found for a directory
and a label, the corresponding commit of the submodule will be referenced.

As far as thirdparties are concerned, I went to a [NuGet](https://github.com/nuget/home)-based solution instead, but old commits still refer the submodules.

## Credits
Forked from https://github.com/lanfeust69

## Changes from lanfeust69 version
Made minor changes to bring in some strange files present, relating to files visible om \main\0
