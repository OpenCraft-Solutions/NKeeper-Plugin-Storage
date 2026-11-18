#!/bin/sh
# Stands in for uname: says Windows' answer (as Git's or Cygwin's uname does) when /var/lib/fake-linux/not-linux
# exists, so the tools can be tried on a host that isn't Linux; otherwise the real one.
if [ -e /var/lib/fake-linux/not-linux ]; then echo MINGW64_NT-10.0-26100; exit 0; fi
exec /bin/uname "$@"
