@echo off
echo === Container rodando ===
docker ps --filter "name=mtrx-web" --format "table {{.Names}}\t{{.Image}}\t{{.Status}}\t{{.CreatedAt}}"

echo.
echo === Imagem mais recente ===
docker images --filter "reference=*web*" --format "table {{.Repository}}\t{{.ID}}\t{{.CreatedAt}}"

echo.
echo === Image ID do container vs imagem ===
for /f "tokens=*" %%i in ('docker inspect mtrx-web --format "{{.Image}}" 2^>nul') do echo Container usa: %%i
for /f "tokens=*" %%i in ('docker images --filter "reference=*web*" --format "{{.ID}}" 2^>nul') do (
    echo Imagem disponivel: %%i
    goto :done_images
)
:done_images

echo.
echo === LoginScreen.tsx dentro do container (procurando EyeIcon) ===
docker exec mtrx-web sh -c "if [ -f /app/src/components/LoginScreen.tsx ]; then grep -c EyeIcon /app/src/components/LoginScreen.tsx; else echo 'dev mount nao ativo (modo prod)'; fi" 2>nul
if errorlevel 1 echo container parece estar em modo prod (sem /app/src)

echo.
echo === dist servido pelo nginx (modo prod) ===
docker exec mtrx-web sh -c "if [ -d /usr/share/nginx/html ]; then ls -la /usr/share/nginx/html/assets/ | head -5; fi" 2>nul

echo.
echo === Resposta da raiz / em localhost:5173 ===
curl -s -o nul -w "HTTP %%{http_code} | size %%{size_download} bytes | etag %%{etag}\n" http://localhost:5173/
