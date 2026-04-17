@echo off
setlocal
set "ONEAPI_ROOT=C:\Program Files (x86)\Intel\oneAPI"
set "MKLROOT=%ONEAPI_ROOT%\mkl\latest"
set "CMPLR_ROOT=%ONEAPI_ROOT%\compiler\latest"
set "TBBROOT=%ONEAPI_ROOT%\tbb\latest"
set "PATH=%MKLROOT%\bin;%CMPLR_ROOT%\bin;%TBBROOT%\bin;%PATH%"
cd /d "%~dp0"
cmake --build build --config Release -- /m
endlocal
