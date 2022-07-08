#build stage
FROM mcr.microsoft.com/dotnet/sdk:6.0-alpine3.14 AS builder

RUN git clone -b dev https://github.com/JJPool/miningcore /miningcore

WORKDIR /miningcore
RUN apk add --no-cache build-base cmake boost-dev libsodium-dev libzmq openssl-dev pkgconfig \
&& cd src/Miningcore/ \
&& dotnet publish -c Release --framework net6.0 -o ../../build/ \
&& mkdir /usr/local/miningcore/ \
&& cd ../../ \
&& mv build/* /usr/local/miningcore/

#Final image
FROM mcr.microsoft.com/dotnet/aspnet:6.0-alpine3.14

LABEL maintainer="JJPOOL"
LABEL description="Docker image for miningcore"

COPY --from=builder /usr/local/miningcore/ /miningcore/

RUN apk add --no-cache boost-date_time boost-system openssl

ENTRYPOINT dotnet /miningcore/Miningcore.dll -c /config.json