#!/bin/sh
# make sure running this script under root permission

rootpath="`pwd`/SiteExtensions/Kudu"

export WEBSITE_SITE_NAME=kudutry
export APPSETTING_SCM_USE_LIBGIT2SHARP_REPOSITORY=0
#export APPSETTING_SCM_REPOSITORY_PATH=wwwroot
export KUDU_APPPATH=$rootpath
export KUDU_MSBUILD=/usr/bin/xbuild
export APPDATA=~/dlocal/AppData
mkdir -p ~/dlocal/AppData
export SCM_BIN_PATH="`pwd`/SiteExtensions/Kudu/bin"

#npm install -g kudusync

rm -f ./kudu.zip
rm -rf ./SiteExtensions
wget http://kudutry.azurewebsites.net/kudu.zip
unzip kudu.zip
cat ./SiteExtensions/Kudu/Web.config | sed 's|  <location path="." inheritInChildApplications="false">|  <location path="~/repo/SiteExtensions/Kudu" inheritInChildApplications="false">|' > ./SiteExtensions/Kudu/Web.config2
mv ./SiteExtensions/Kudu/Web.config2 ./SiteExtensions/Kudu/Web.config


chmod 777 ./SiteExtensions/Kudu/bin/kudu.exe
mkdir -p ~/site/repository/.git/hooks
touch ~/site/repository/.git/hooks/post-receive
chmod 777 ~/site/repository/.git/hooks/post-receive
chmod 777 ./SiteExtensions/Kudu/bin/node_modules/.bin/kuduscript


cd $rootpath
xsp4
