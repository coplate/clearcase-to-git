use strict;
my $type = shift;
my $vob = shift;
my $project = shift;
my $list_of_files = shift;
my $file_ending = qr/\@\@(\\main(\\[\w\.]+)*\\\d+)?\r$/;
my @dir_patterns = (
  # we need to know all interesting elements, even if they exist only in checkedout directories, but we skip checkedout *versions* (incremental export)
  qr/CHECKEDOUT\r$/,
  # general directories
  qr/ORIGINAL_SOURCE/,
  qr/(DATABASE|database)/,
  qr/transfer/,
  qr/mockups/,
  qr/cgi-logs/
);
my @file_patterns = (
  # files
  qr/README\.md/,
  qr/\.bak$/, # infidividual file patters
  qr/[\\]ORIGINAL_SOURCE[\\]/,  # files in forbitten directories
  qr/[\\](DATABASE|database)[\\]/,
  qr/[\\]transfer[\\]/,
  qr/[\\]mockups[\\]/,
  qr/[\\]cgi-logs[\\]/
);
my $patterns = \@dir_patterns;
if( $type eq 'F' ){
  $patterns = \@file_patterns;
}
my $seen_dot = 0;
print STDERR "Removing ^$vob\\$project\r\n";
open(my $fh, '<', $list_of_files) || die "cannot open $_";
while(my $path = <$fh>){
  $path =~ s/^\Q$vob\E[\\]//i; # turn  #K:\view\vob\project@@ into project@@, K:\view\vob\.@@ to .@@
        
        
  my $skip = 0;
  #print $path."\r\n";
  if( not $path =~ '^[.][@][@]' ){
    if( not $path =~ /^\Q$project\E/i ){
      next; # skip lost_+found, airbp, etc
    }else{
      if( $seen_dot == 0 ){
        print '.@@'."\r\n"; # for certain reasons, the script does not work reliably if the parent folder K:\view\vob\.@@ is n ot present, so this adds it.
        $seen_dot = 1;
      }
    }
  }else{
    $seen_dot = 1;
  }
       
       
  foreach my $pattern (@{$patterns}) {
    if ($path =~ /$pattern/) {
      $skip = 1;
      last;
    }
  }
        
  next if $skip;
  print $path;
 }
